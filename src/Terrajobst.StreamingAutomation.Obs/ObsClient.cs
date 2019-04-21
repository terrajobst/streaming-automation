using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Terrajobst.StreamingAutomation.Obs
{
    public sealed class ObsClient : IDisposable
    {
        private TcpClient _client;
        private readonly LineDispatcher _lineDispatcher;
        private readonly SynchronizationContext _synchronizationContext;
        private readonly JsonSerializerSettings _serializerSettings;

        private ObsClient(TcpClient client)
        {
            _client = client;
            _lineDispatcher = new LineDispatcher(_client.GetStream());
            _synchronizationContext = SynchronizationContext.Current;

            _serializerSettings = new JsonSerializerSettings();
            _serializerSettings.ContractResolver = new DefaultContractResolver()
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            };

            _lineDispatcher.Start();
            _lineDispatcher.OnData += MarshalObsEvent;
        }

        public void Dispose()
        {
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
        }

        public static async Task<ObsClient> ConnectAsync(int port = 28195, string host = "localhost")
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port);
                return new ObsClient(client);
            }
            catch (SocketException)
            {
                var message = "Cannot connect to OBS. Ensure it's started and that you've got the Stream Deck plug-in installed.";
                throw new InvalidOperationException(message);
            }
        }

        public async Task<(bool IsStreaming, bool IsRecording)> GetStatusAsync()
        {
            var request = new JsonRpcRequest { Id = 31 };
            var response = await SendMessageAsync<JsonRpcResponse<StatusResult>>(request);
            var isStreaming = response.Result.StreamingStatus == "live";
            var isRecording = response.Result.RecordingStatus == "recording";
            return (isStreaming, isRecording);
        }

        public async Task<bool> StartStreamingAsync()
        {
            var request = new JsonRpcRequest { Id = 1 };
            var response = await SendMessageAsync<JsonRpcResponse>(request);
            OnStreamingStatusChanged();
            return !response.Error;
        }

        public async Task<bool> StopStreamingAsync()
        {
            var request = new JsonRpcRequest { Id = 2 };
            var firstResult = await SendMessageAsync<JsonRpcResponse>(request);

            // Sometimes Stream Deck doesn't send events when shutting down. So
            // let's call this function until we receive an error, meaning streaming
            // is already shutdown.

            if (!firstResult.Error)
            {
                JsonRpcResponse response;
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    response = await SendMessageAsync<JsonRpcResponse>(request);
                }
                while (!response.Error);
            }

            OnStreamingStatusChanged();
            return !firstResult.Error;
        }

        public async Task<bool> StartRecordingAsync()
        {
            var request = new JsonRpcRequest { Id = 3 };
            var response = await SendMessageAsync<JsonRpcResponse>(request);
            OnRecordingStatusChanged();
            return !response.Error;
        }

        public async Task<bool> StopRecordingAsync()
        {
            var request = new JsonRpcRequest { Id = 4 };
            var response = await SendMessageAsync<JsonRpcResponse>(request);
            OnRecordingStatusChanged();
            return !response.Error;
        }

        public async Task<string[]> GetSceneCollectionIdsAsync()
        {
            var request = new JsonRpcRequest { Id = 8 };
            var response = await SendMessageAsync<JsonRpcResponse<IdCollectionResult>>(request);
            return response.Result.Data.Select(s => s.Id).ToArray();
        }

        public async Task<bool> SelectSceneCollectionAsync(string sceneCollectionId)
        {
            // It looks like you don't get a response unless the current scene is
            // different. So let's guard for this.

            var current = await GetSelectedSceneCollectionIdAsync();
            if (current == sceneCollectionId)
                return false;

            var request = new JsonRpcRequest
            {
                Id = 6,
                Params = new JsonRpcParams
                {
                    Args = new[] { sceneCollectionId }
                }
            };
            var response = await SendMessageAsync<JsonRpcResponse>(request);
            OnSelectedSceneCollectionChanged();
            return !response.Error;
        }

        public async Task<string> GetSelectedSceneCollectionIdAsync()
        {
            var request = new JsonRpcRequest { Id = 27 };
            var response = await SendMessageAsync<JsonRpcResponse<IdResult>>(request);
            return response.Result.Id;
        }

        public async Task<ObsScene[]> GetScenesAsync(string sceneCollectionId = "")
        {
            var request = new JsonRpcRequest
            {
                Id = 9,
                Params = new JsonRpcParams
                {
                    Args = new[] { sceneCollectionId }
                }
            };
            var response = await SendMessageAsync<JsonRpcResponse<ObsScene[]>>(request);
            return response.Result;
        }

        public async Task<bool> SelectSceneAsync(string sceneId)
        {
            var request = new JsonRpcRequest
            {
                Id = 11,
                Params = new JsonRpcParams
                {
                    Args = new[] { sceneId }
                }
            };
            var response = await SendMessageAsync<JsonRpcResponse>(request);
            OnSelectedSceneChanged();
            return !response.Error;
        }

        public async Task<string> GetSelectedSceneIdAsync()
        {
            var request = new JsonRpcRequest { Id = 12 };
            var response = await SendMessageAsync<JsonRpcResponse<string>>(request);
            return response.Result;
        }

        public async Task<ObsSource[]> GetSourcesAsync(string sceneId = "")
        {
            var request = new JsonRpcRequest
            {
                Id = 10,
                Params = new JsonRpcParams
                {
                    Args = new[] { sceneId }
                }
            };
            var response = await SendMessageAsync<JsonRpcResponse<ObsSource[]>>(request);
            return response.Result;
        }

        private async Task<string> SendMessageAsync(string message, int id)
        {
            if (_client == null)
                throw new ObjectDisposedException(nameof(ObsClient));

            var taskCompletionSource = new TaskCompletionSource<string>();

            void Handler(string jsonText)
            {
                var json = JObject.Parse(jsonText);
                var responseId = json["id"]?.Value<int?>();
                if (responseId != id)
                    return;

                var result = json["result"];
                if (result != null && result.Type == JTokenType.Object && result["_type"]?.Value<string>() == "HELPER")
                    return;

                _lineDispatcher.OnData -= Handler;
                taskCompletionSource.SetResult(jsonText);
            }

            _lineDispatcher.OnData += Handler;

            var stream = _client.GetStream();

            using (var writer = new StreamWriter(stream, Encoding.UTF8, 4096, leaveOpen: true))
            {
                Debug.WriteLine("SENT     : " + message);
                await writer.WriteLineAsync(message);
            }

            return await taskCompletionSource.Task;
        }

        private async Task<TResponse> SendMessageAsync<TResponse>(JsonRpcRequest message)
        {
            var messageText = JsonConvert.SerializeObject(message, _serializerSettings);
            var responseText = await SendMessageAsync(messageText, message.Id);
            return JsonConvert.DeserializeObject<TResponse>(responseText, _serializerSettings);
        }

        private void MarshalObsEvent(string json)
        {
            if (_synchronizationContext == null)
                OnObsEvent(json);
            else
                _synchronizationContext.Post(s => OnObsEvent(json), null);
        }

        private void OnObsEvent(string json)
        {
            var data = JObject.Parse(json);

            var result = data["result"];
            if (result == null)
                return;

            if (result.Type != JTokenType.Object)
                return;

            var type = result["_type"]?.Value<string>();
            if (type != "EVENT")
                return;

            Debug.WriteLine("DISPATCH : " + json);

            var resourceId = result["resourceId"]?.Value<string>();
            switch (resourceId)
            {
                case "StreamingService.streamingStatusChange":
                    OnStreamingStatusChanged();
                    break;
                case "StreamingService.recordingStatusChange":
                    OnRecordingStatusChanged();
                    break;
                case "SceneCollectionsService.collectionSwitched":
                    OnSelectedSceneCollectionChanged();
                    break;
                case "ScenesService.sceneSwitched":
                    OnSelectedSceneChanged();
                    break;
            }
        }

        private void OnStreamingStatusChanged()
        {
            StreamingStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnRecordingStatusChanged()
        {
            RecordingStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSelectedSceneCollectionChanged()
        {
            SelectedSceneCollectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSelectedSceneChanged()
        {
            SelectedSceneChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler StreamingStatusChanged;

        public event EventHandler RecordingStatusChanged;

        public event EventHandler SelectedSceneCollectionChanged;

        public event EventHandler SelectedSceneChanged;

        private sealed class LineDispatcher
        {
            private readonly NetworkStream _stream;

            public LineDispatcher(NetworkStream stream)
            {
                _stream = stream;
            }

            public void Start()
            {
                Task.Run(WorkerAsync);
            }

            private async Task WorkerAsync()
            {
                try
                {
                    using (var reader = new StreamReader(_stream, Encoding.UTF8,
                                                         detectEncodingFromByteOrderMarks: false,
                                                         4096,
                                                         leaveOpen: true))
                    {
                        while (true)
                        {
                            var line = await reader.ReadLineAsync();
                            if (line == null)
                                return;

                            Debug.WriteLine("RECEIVED : " + line);
                            OnData?.Invoke(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("WORKER CRASHED: " + ex.Message);
                }
            }

            public event Action<string> OnData;
        }

        private abstract class JsonRpc
        {
            public string Jsonrpc { get; set; } = "2.0";
        }

        private sealed class JsonRpcRequest : JsonRpc
        {
            public int Id { get; set; }

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public JsonRpcParams Params { get; set; }
        }

        public sealed class JsonRpcParams
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string[] Args { get; set; }
        }

        private sealed class JsonRpcResponse : JsonRpc
        {
            public bool Error { get; set; }
        }

        private sealed class JsonRpcResponse<T> : JsonRpc
        {
            public bool Error { get; set; }
            public T Result { get; set; }
        }

        private sealed class StatusResult
        {
            public string RecordingStatus { get; set; }
            public string StreamingStatus { get; set; }
        }

        private sealed class IdResult
        {
            public string Id { get; set; }
        }

        private sealed class IdCollectionResult
        {
            public IdResult[] Data { get; set; }
        }
    }

    public sealed class ObsScene
    {
        public string Id { get; set; }

        public string Name { get; set; }

        [JsonProperty("nodes")]
        public ObsSceneItem[] Items { get; set; }

        public override string ToString() => Name;
    }

    public sealed class ObsSceneItem
    {
        public string ParentId { get; set; }
        public int SceneItemId { get; set; }
        public string SceneNodeType { get; set; }
        public string SourceId { get; set; }
        public bool Visible { get; set; }
        public string Name { get; set; }
        public string[] ChildrenIds { get; set; }

        public override string ToString() => Name;
    }

    public sealed class ObsSource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Audio { get; set; }
        public bool Muted { get; set; }
        public bool Group { get; set; }

        public override string ToString() => Name;
    }
}
