# Live Stream Automation

[![Build Status](https://terrajobst.visualstudio.com/streaming-automation/_apis/build/status/terrajobst.streaming-automation?branchName=master)](https://terrajobst.visualstudio.com/streaming-automation/_build/latest?definitionId=16&branchName=master)

## OBS

 [![nuget](https://img.shields.io/nuget/v/Terrajobst.StreamingAutomation.Obs.svg)](https://www.nuget.org/packages/Terrajobst.StreamingAutomation.Obs/)

This allows you to automate OBS Studio. In order to use it, you need to install
the [Stream Deck] software. You don't have to have the Stream Deck connected to
your computer though.

[Stream Deck]: https://gc-updates.elgato.com/windows/sd-update/final/download-website.php

For example, let's say you want to start your stream on a specific scene and
always want to record and stream, you'd write code like this:

```C#
private void StartStreamButton_Click(object sender, EventArgs e)
{
    var sceneCollectionId = "My Scene Collection";
    var introSceneId = sceneCollectionId + "My Intro Scene";

    var obsClient = await ObsClient.ConnectAsync();
    await obsClient.SelectSceneCollectionAsync(sceneCollectionId);
    await obsClient.SelectSceneAsync(introSceneId);
    await obsClient.StartRecordingAsync();
    await obsClient.StartStreamingAsync();
}
```

Similarly, when you want to end your stream by fading to black, you can write
code like this:

```C#
private void StopStreamButton_Click(object sender, EventArgs e)
{
    var sceneCollectionId = "My Scene Collection";
    var blankSceneId = sceneCollectionId + "My Blank Scene";

    var obsClient = await ObsClient.ConnectAsync();

    await obsClient.SelectSceneCollectionAsync(sceneCollectionId);
    await obsClient.SelectSceneAsync(blankSceneId);

    await Task.Delay(TimeSpan.FromSeconds(2));

    await obsClient.StopStreamingAsync();
    await obsClient.StopRecordingAsync();
}
```

## YouTube

[![nuget](https://img.shields.io/nuget/v/Terrajobst.StreamingAutomation.YouTube.svg)](https://www.nuget.org/packages/Terrajobst.StreamingAutomation.YouTube/)

After you started the OBS stream, you probably want to start the broad cast on a
streaming platform like YouTube. If you can use YouTube Stream Now, then
starting the broadcast explicitly isn't needed. But if you use multiple events,
you may need to start the event manually.

You can easily automate this like this:

```C#
const string youTubeClientId = "<Your YouTube Client ID";
const string youTubeClientSecret = "<Your YouTube Client Secrete";
var youTubeClient = await YouTubeClient.ConnectAsync(youTubeClientId, youTubeClientSecret);

var upcomingEvents = await youTubeClient.GetUpcomingEvents();
var selectEvent = ChooseEvent(upcomingEvents);

// Use OBS to start the stream
await Task.Delay(TimeSpan.FromSeconds(5));

// Start the broadcast on the YouTube side:
await youTubeClient.StartBroadcast(selectEvent.Id);

// To stop the broadcast, you'd call this method:
await youTubeClient.EndBroadcast(selectEvent.Id);
```
