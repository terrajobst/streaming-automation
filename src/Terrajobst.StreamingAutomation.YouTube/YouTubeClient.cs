using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;

using Humanizer;

using BroadcastStatus = Google.Apis.YouTube.v3.LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum;

namespace Terrajobst.StreamingAutomation.YouTube
{
    public sealed class YouTubeClient
    {
        private readonly YouTubeService _service;

        private YouTubeClient(YouTubeService service)
        {
            _service = service;
        }

        public static async Task<YouTubeClient> ConnectAsync(string clientId, string clientSecret)
        {
            if (clientId is null)
                throw new ArgumentNullException(nameof(clientId));

            if (clientSecret is null)
                throw new ArgumentNullException(nameof(clientSecret));

            var secrets = new ClientSecrets()
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            };
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                // This OAuth 2.0 access scope allows for full read/write access to the
                // authenticated user's account.
                new[] {
                    // YouTubeService.Scope.Youtube,
                    // YouTubeService.Scope.YoutubeUpload,
                    YouTubeService.Scope.YoutubeForceSsl
                },
                "user",
                CancellationToken.None
            );

            var initializer = new BaseClientService.Initializer
            {
                HttpClientInitializer = credential
            };

            var service = new YouTubeService(initializer);
            return new YouTubeClient(service);
        }

        public async Task<IReadOnlyList<YouTubeEvent>> GetUpcomingEvents()
        {
            var result = new List<YouTubeEvent>();
            var list = _service.LiveBroadcasts.List("id,snippet,status");
            list.BroadcastStatus = LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Upcoming;
            var response = await list.ExecuteAsync();

            foreach (var item in response.Items)
            {
                if (item.Status.LifeCycleStatus != "ready")
                    continue;

                var id = item.Id;
                var title = item.Snippet.Title;
                var scheduledStartTime = item.Snippet.ScheduledStartTime;
                var scheduledEndTime = item.Snippet.ScheduledEndTime;

                var youTubeEvent = new YouTubeEvent(id, title, scheduledStartTime, scheduledEndTime);
                result.Add(youTubeEvent);
            }

            return result.ToArray();
        }

        public async Task StartBroadcast(string eventId)
        {
            await Transition(eventId, BroadcastStatus.Testing);
            await Transition(eventId, BroadcastStatus.Live);
        }

        public async Task EndBroadcast(string eventId)
        {
            await Transition(eventId, BroadcastStatus.Complete);
        }

        private async Task Transition(string eventId, BroadcastStatus status)
        {
            var testRequest = _service.LiveBroadcasts.Transition(status, eventId, "id");
            await testRequest.ExecuteAsync();

            var lifeCycleStatus = ToLifeCycleStatus(status);

            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                var request = _service.LiveBroadcasts.List("id, status");
                request.Id = eventId;
                var response = await request.ExecuteAsync();
                var item = response.Items.Single();
                if (item.Status.LifeCycleStatus == lifeCycleStatus)
                    return;

                Debug.WriteLine($"Current: {item.Status.LifeCycleStatus}, Waiting For: {lifeCycleStatus}");
            }
        }

        private static string ToLifeCycleStatus(BroadcastStatus status)
        {
            switch (status)
            {
                case BroadcastStatus.Testing:
                    return "testing";
                case BroadcastStatus.Live:
                    return "liveStarting";
                case BroadcastStatus.Complete:
                    return "complete";
                default:
                    throw new NotImplementedException();
            }
        }

        public async Task<bool> IsLive(string eventId)
        {
            var request = _service.LiveBroadcasts.List("id, status");
            request.Id = eventId;
            var response = await request.ExecuteAsync();
            var item = response.Items.Single();
            return item.Status.LifeCycleStatus == "live";
        }
    }

    public sealed class YouTubeEvent
    {
        public YouTubeEvent(string id, string title, DateTime? scheduledStartTime, DateTime? scheduledEndTime)
        {
            Id = id;
            Title = title;
            ScheduledStartTime = scheduledStartTime;
            ScheduledEndTime = scheduledEndTime;

            if (scheduledStartTime != null)
            {
                var sb = new StringBuilder();

                if (ScheduledStartTime <= DateTime.Now)
                    sb.Append("Started ");
                else
                    sb.Append("Starts in ");

                sb.Append(ScheduledStartTime.Humanize(false));

                if (ScheduledEndTime != null)
                {
                    var duration = ScheduledEndTime.Value - ScheduledStartTime.Value;
                    sb.Append(" (");
                    sb.Append(duration.Humanize());
                    sb.Append(")");
                }

                ScheduleText = sb.ToString();
            }
        }

        public string Id { get; }
        public string Title { get; }
        public DateTime? ScheduledStartTime { get; }
        public DateTime? ScheduledEndTime { get; }
        public string ScheduleText { get; }

        public override string ToString()
        {
            if (ScheduleText == null)
                return Title;

            return Title + " " + ScheduleText;
        }
    }
}
