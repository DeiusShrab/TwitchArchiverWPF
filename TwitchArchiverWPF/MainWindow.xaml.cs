﻿using HandyControl.Controls;
using Microsoft.WindowsAPICodePack.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using TwitchArchiverWPF.Settings;
using TwitchArchiverWPF.TwitchObjects;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Options;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using Xabe.FFmpeg.Downloader;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;
using Path = System.IO.Path;
using Window = System.Windows.Window;

namespace TwitchArchiverWPF
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window
  {
    PageStreamers pageStreamers = new PageStreamers();
    PageVods pageVods = new PageVods();
    PageSettings pageSettings = new PageSettings();
    BackgroundWorker backgroundWorker = new BackgroundWorker();
    ILogger? mainLogger = null;

    public static string fileDatePattern = @"yyyy-MM-ddTHHmmss";
    public static string logDatePattern = @"yyyy-MM-dd HH:mm:ss.fff";

    public MainWindow()
    {
      String processName = Process.GetCurrentProcess().ProcessName;

      if (Process.GetProcesses().Count(p => p.ProcessName == processName) > 1)
        System.Windows.Application.Current.Shutdown();

      if (!File.Exists("ffmpeg.exe"))
        FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);

      InitializeComponent();

      Task.Run(backgroundWorker_DoWork);
    }

    private void backgroundWorker_DoWork()
    {
      Dictionary<string, AccessToken> accessTokens = new Dictionary<string, AccessToken>();
      Dictionary<string, Broadcast> broadcastList = new Dictionary<string, Broadcast>();
      int checkCount = 0;
      using Logger bgWorkerLogger = new LoggerConfiguration().WriteTo.File(Path.Combine(Settings.Settings.Default.TempFolder, ".log", $"Main_{DateTime.Now.ToString(fileDatePattern)}.log")).CreateLogger();
      mainLogger = bgWorkerLogger;

      while (true)
      {
        foreach (var streamId in broadcastList.Keys)
        {
          if (broadcastList[streamId].DownloadTask.IsCompleted)
          {
            broadcastList.Remove(streamId);
          }
        }

        List<Streamer>? streamerList = JsonConvert.DeserializeObject<List<Streamer>>(Settings.Settings.Default.Streamers);
        GlobalSettings? globalSettings = JsonConvert.DeserializeObject<GlobalSettings>(Settings.Settings.Default.GlobalSettings);
        if (streamerList != null && globalSettings != null)
        {
          foreach (var streamer in streamerList)
          {
            //Check if streamer is live every x seconds setting
            int liveCheck = streamer.OverrideRecordingSettings ? streamer.RecordingSettings.LiveCheck : globalSettings.RecordingSettings.LiveCheck;
            if (checkCount % liveCheck == 0)
            {
              bool updatedToken = true;
              DateTime expireCheck = DateTime.Now.AddSeconds(liveCheck > 300 ? liveCheck : 300); // 5min or next LiveCheck, whichever is longer
              if (!accessTokens.ContainsKey(streamer.Id) || accessTokens[streamer.Id].expires < expireCheck)
              {
                updatedToken = RefreshToken(streamer, accessTokens, globalSettings);
              }

              //If token fails to update then don't try and check if live
              if (updatedToken)
              {
                try
                {
                  LogInformation($"CHECKING {streamer.Name}");
                  if (broadcastList.Values.Any(x => x.StreamerId == streamer.Id && !x.DownloadTask.IsCompleted))
                  {
                    LogInformation($"{streamer.Name} is still LIVE");
                    continue;
                  }

                  using WebClient playlistClient = new WebClient();
                  playlistClient.Headers.Add("Accept", "application/x-mpegURL, application/vnd.apple.mpegurl, application/json, text/plain");
                  playlistClient.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0");
                  string[]? playlistList = null;
                  if ((streamer.OverrideRecordingSettings && streamer.RecordingSettings.EnableTtv) || (!streamer.OverrideRecordingSettings && globalSettings.RecordingSettings.EnableTtv))
                  {
                    playlistClient.Headers.Add("X-Donate-To", "https://ttv.lol/donate");
                    playlistList = playlistClient.DownloadString("https://api.ttv.lol/playlist/" + HttpUtility.UrlEncode($"{streamer.Name.ToLower()}.m3u8?allow_source=true&fast_bread=true&player_backend=mediaplayer&playlist_include_framerate=true&sig={accessTokens[streamer.Id].signature}&token={accessTokens[streamer.Id].value}")).Split('\n');
                  }
                  else
                  {
                    playlistList = playlistClient.DownloadString($"https://usher.ttvnw.net/api/channel/hls/{streamer.Name.ToLower()}.m3u8?allow_source=true&fast_bread=true&player_backend=mediaplayer&playlist_include_framerate=true&sig={accessTokens[streamer.Id].signature}&token={HttpUtility.UrlEncode(accessTokens[streamer.Id].value)}").Split('\n');
                  }
                  string streamId = playlistList.First(x => x.Contains("BROADCAST-ID=")).Split(',').First(x => x.Contains("BROADCAST-ID=")).Replace("BROADCAST-ID=", "").Replace("\"", "");
                  double serverTime = double.Parse(playlistList.First(x => x.Contains("SERVER-TIME=")).Split(',').First(x => x.Contains("SERVER-TIME=")).Replace("SERVER-TIME=", "").Replace("\"", ""));
                  double streamTime = double.Parse(playlistList.First(x => x.Contains("STREAM-TIME=")).Split(',').First(x => x.Contains("STREAM-TIME=")).Replace("STREAM-TIME=", "").Replace("\"", ""));
                  string streamQuality = playlistList.First(x => x.Contains("NAME=")).Split(',').First(x => x.Contains("NAME=")).Replace("NAME=", "").Replace("\"", "");
                  DateTime startTime = DateTimeOffset.FromUnixTimeSeconds((int)(serverTime - streamTime)).DateTime;
                  string playlist = playlistList.First(x => !x.StartsWith('#'));

                  if (!broadcastList.ContainsKey(streamId) && streamQuality.Contains("(source)"))
                  {
                    LogInformation($"DETECTED {streamer.Name} LIVE");
                    Task downloadTask = Task.Run(() => DownloadTask(streamer, globalSettings, playlist, streamId, startTime));
                    broadcastList.Add(streamId, new Broadcast(streamId, streamer.Id, downloadTask));
                  }
                }
                catch (WebException ex)
                {
                  HttpWebResponse? res = (HttpWebResponse?)ex.Response;
                  if (res == null)
                  {
                    LogError(ex, "WebException NULL");
                  }
                  else if (res.StatusCode == HttpStatusCode.Forbidden)
                  {
                    LogInformation("Access Token timed out, refreshing");

                    try
                    {
                      RefreshToken(streamer, accessTokens, globalSettings);
                    }
                    catch (Exception e)
                    {
                      LogError(e, "Exception during RefreshToken");
                    }
                  }
                  else if (res.StatusCode == HttpStatusCode.NotFound)
                  {
                    // Ignore this one, it's normal
                  }
                  else
                  {
                    LogError(ex, "WebException");
                  }
                }
              }
            }
          }
        }
        checkCount++;
        Thread.Sleep(1000);
      }
    }

    private void DownloadTask(Streamer streamer, GlobalSettings globalSettings, string playlistUrl, string streamId, DateTime startTime)
    {
      if (!Directory.Exists(Settings.Settings.Default.SaveFolder))
        Directory.CreateDirectory(Settings.Settings.Default.SaveFolder);

      string streamerFolder = Path.Combine(Settings.Settings.Default.SaveFolder, $"{streamer.Name}_{streamer.Id}");
      if (!Directory.Exists(streamerFolder))
        Directory.CreateDirectory(streamerFolder);

      string format = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
      string dateString = DateTime.Now.ToString(format).Replace("/", "-");
      string downloadFolder = Path.Combine(Settings.Settings.Default.TempFolder, $"Download_{streamer.Name}_{streamId}");
      string finalFolder = Path.Combine(Settings.Settings.Default.SaveFolder, streamerFolder, $"{dateString}_{streamId}");
      string liveDirectory = Path.Combine(downloadFolder, "Live");
      string vodDirectory = Path.Combine(downloadFolder, "VOD");

      if (!Directory.Exists(downloadFolder))
        Directory.CreateDirectory(downloadFolder);
      using Logger downloadLogger = new LoggerConfiguration().WriteTo.File(Path.Combine(downloadFolder, $"Download_{DateTime.Now.ToString(fileDatePattern)}.log")).CreateLogger();

      try
      {
        LogInformation($"Detected {streamer.Name} LIVE", downloadLogger);
        List<string> livePartsList = new List<string>();

        CancellationTokenSource liveCancel = new CancellationTokenSource();
        Task? liveTask = null;
        CancellationTokenSource liveChatCancel = new CancellationTokenSource();
        Task? liveChatTask = null;
        CancellationTokenSource vodCancel = new CancellationTokenSource();
        Task? vodTask = null;
        CancellationTokenSource infoCancel = new CancellationTokenSource();
        Task<StreamInfoTaskResponse>? infoTask = null;
        string? vodId = null;
        string? firstPlaylist = null;

        infoTask = Task.Run<StreamInfoTaskResponse>(() => DownloadInfoTask(streamer, infoCancel.Token, downloadLogger));

        if (streamer.DownloadOptions.DownloadLiveStream && streamer.DownloadOptions.DownloadLiveChat)
        {
          Exception e = null;
          int tryCount = 0;
          while (firstPlaylist == null && tryCount < 5)
          {
            try
            {
              firstPlaylist = new WebClient().DownloadString(playlistUrl);
            }
            catch
            {
              tryCount++;
              Thread.Sleep(1000);
            }
          }

          if (firstPlaylist == null)
          {
            LogError(e, $"Failed to download after {tryCount} attempts", downloadLogger);
          }
        }

        if (streamer.DownloadOptions.DownloadLiveStream)
        {
          if (!Directory.Exists(liveDirectory))
            Directory.CreateDirectory(liveDirectory);
          else
          {
            try
            {
              string oldFilesDirectory = Path.Combine(liveDirectory, "old");
              List<string> oldFiles = new List<string>(Directory.GetFiles(liveDirectory, "*.ts"));
              if (oldFiles.Count > 0)
              {
                LogInformation("Detected previous stream segments, moving to \"old\" folder to prevent gap in stream", downloadLogger);
                if (!Directory.Exists(oldFilesDirectory))
                  Directory.CreateDirectory(oldFilesDirectory);

                foreach (var oldFile in oldFiles)
                {
                  try
                  {
                    File.Move(oldFile, Path.Combine(oldFilesDirectory, Path.GetFileName(oldFile)));
                  }
                  catch
                  {
                    try
                    {
                      File.Delete(oldFile);
                    }
                    catch (Exception ex)
                    {
                      LogError(ex, $"Exception deleting {oldFile}", downloadLogger);
                    }
                  }
                }
              }
            }
            catch (Exception ex)
            {
              LogError(ex, "Error moving previous segments to \"old\" folder", downloadLogger);
            }
          }

          liveTask = Task.Run(() => DownloadLiveTask(playlistUrl, firstPlaylist, livePartsList, liveDirectory, downloadLogger, liveCancel.Token, downloadLogger));
        }

        if (streamer.DownloadOptions.DownloadLiveChat)
        {
          if (!Directory.Exists(liveDirectory))
            Directory.CreateDirectory(liveDirectory);

          liveChatTask = Task.Run(() => DownloadLiveChatTask(streamer, liveDirectory, playlistUrl, firstPlaylist, startTime, liveChatCancel.Token, downloadLogger));
        }

        if (streamer.DownloadOptions.DownloadVodStream || streamer.DownloadOptions.DownloadVodChat)
        {
          bool gotVod = false;
          int failCount = 0;
          while (!gotVod)
          {
            using WebClient apiClient = new WebClient();
            apiClient.Headers["Content-Type"] = "application/json";
            apiClient.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            StreamGqlData? streamInfo = JsonConvert.DeserializeObject<StreamGqlData>(apiClient.UploadString("https://gql.twitch.tv/gql", "{\"query\":\"query{user(id:\\\"" + streamer.Id + "\\\"){id,login,displayName,videos(type:ARCHIVE){edges{node{id,title,game{name},broadcastType,status,createdAt}}},stream{archiveVideo{id,status}id,title,type,viewersCount,createdAt,game{name}}}}\",\"variables\":{}}"));

            if (streamInfo != null && streamInfo.data != null && streamInfo.data.user != null)
            {
              if (streamInfo.data.user.stream == null || streamInfo.data.user.stream.id != streamId)
              {
                failCount++;
              }
              else
              {
                if (streamInfo.data.user.stream.archiveVideo == null || streamInfo.data.user.stream.archiveVideo.id == null)
                {
                  //VOD doesn't exist
                  LogError(null, $"No associated VOD with stream, streamer maybe has VODs disabled", downloadLogger);
                  gotVod = true;
                }
                else if (streamInfo.data.user.stream.archiveVideo.id != null)
                {
                  vodId = streamInfo.data.user.stream.archiveVideo.id!;
                  if (streamer.DownloadOptions.DownloadVodStream)
                  {
                    if (!Directory.Exists(vodDirectory))
                      Directory.CreateDirectory(vodDirectory);

                    string? vodplaylistUrl = GetVodPlaylistUrl(vodId, streamer, globalSettings, downloadLogger);
                    if (vodplaylistUrl != null)
                    {
                      LogInformation("Got VOD playlist URL " + vodplaylistUrl, downloadLogger);
                      gotVod = true;
                      vodTask = Task.Run(() => DownloadVodTask(vodplaylistUrl, vodDirectory, vodId, vodCancel.Token, downloadLogger));
                    }
                    else
                    {
                      LogError(null, "Unable to get VOD playlist (Sub Only VOD?)", downloadLogger);
                      //Start VOD task to check when VOD has ended to download chat
                      vodTask = Task.Run(() => DownloadVodTask(null, vodDirectory, vodId, vodCancel.Token, downloadLogger));
                      gotVod = true;
                    }
                  }
                  else
                  {
                    gotVod = true;
                  }
                }
                else
                {
                  failCount++;
                }
              }
            }

            //Give up after 5 minutes of trying
            if (failCount > 30)
            {
              bool found = false;
              //Lets try checking the VODs from the channel, if the stream is short (< 1 min) it might not have showed up on the user stream response
              if (streamInfo != null && streamInfo.data != null && streamInfo.data.user != null && streamInfo.data.user.videos != null && streamInfo.data.user.videos.edges != null)
              {
                List<Edge> videoList = streamInfo.data.user.videos.edges;
                foreach (var video in videoList)
                {
                  if (video.node != null)
                  {
                    //If the VOD started within 2 minutes of live stream, assume it's the same
                    if (Math.Abs((video.node.createdAt - startTime).TotalSeconds) < 120)
                    {
                      found = true;
                      vodId = video.node.id;
                      if (streamer.DownloadOptions.DownloadVodStream && vodId != null)
                      {
                        if (!Directory.Exists(vodDirectory))
                          Directory.CreateDirectory(vodDirectory);

                        string? vodplaylistUrl = GetVodPlaylistUrl(vodId, streamer, globalSettings, downloadLogger);
                        if (vodplaylistUrl != null)
                        {
                          LogInformation("Got VOD playlist URL " + vodplaylistUrl, downloadLogger);
                          vodTask = Task.Run(() => DownloadVodTask(vodplaylistUrl, vodDirectory, vodId, vodCancel.Token, downloadLogger));
                        }
                        else
                        {
                          LogError(null, "Unable to get VOD playlist", downloadLogger);
                        }
                      }
                      break;
                    }
                  }
                }
              }
              gotVod = true;
              if (!found)
              {
                LogError(null, $"Unable to find associated VOD with stream", downloadLogger);
              }
            }

            if (!gotVod)
              Thread.Sleep(10000);
          }
        }

        StreamMetadata streamMetadata = new StreamMetadata();
        streamMetadata.StreamTime = startTime;
        streamMetadata.StreamerId = streamer.Id;
        streamMetadata.StreamerName = streamer.Name;

        try
        {
          if (vodTask != null)
            vodTask.Wait();
          streamMetadata.VodStreamPath = Path.GetRelativePath(finalFolder, Path.Combine(finalFolder, "VOD", "vod.mp4"));
          if (File.Exists(streamMetadata.VodStreamPath))
          {
            ShellFile shellFile = ShellFile.FromFilePath(Path.Combine(downloadFolder, "VOD", "vod.mp4"));
            if (streamMetadata.Thumbnail == null)
            {
              using MemoryStream ms = new MemoryStream();
              shellFile.Thumbnail.LargeBitmap.Save(ms, ImageFormat.Png);
              streamMetadata.Thumbnail = Convert.ToBase64String(ms.ToArray());
            }
            long? timeTicks = (long?)shellFile.Properties.System.Media.Duration.Value;
            if (timeTicks != null)
            {
              streamMetadata.Length = (int)TimeSpan.FromTicks((long)timeTicks).TotalSeconds;
            }
          }
          else
          {
            LogInformation($"VodTask {streamId} - vod.mp4 does not exist", downloadLogger);
          }
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in DownloadTask near VodTask", downloadLogger);
        }

        List<Task> renderTasks = new List<Task>();

        if (streamer.DownloadOptions.DownloadVodChat && vodId != null)
        {
          Task t = Task.Run(() =>
          {
            try
            {
              ChatDownloadOptions downloadOptions = new ChatDownloadOptions();

              downloadOptions.Id = vodId;
              downloadOptions.IsJson = true;
              downloadOptions.CropBeginning = false;
              downloadOptions.CropBeginningTime = 0.0;
              downloadOptions.CropEnding = false;
              downloadOptions.CropEndingTime = 0.0;
              downloadOptions.Timestamp = false;
              downloadOptions.EmbedEmotes = true;
              downloadOptions.Filename = Path.Combine(downloadFolder, "VOD", "vod_chat.json");
              ChatDownloader chatDownloader = new ChatDownloader(downloadOptions);
              chatDownloader.DownloadAsync(new Progress<ProgressReport>(), new CancellationToken()).Wait();
              streamMetadata.VodChatPath = Path.GetRelativePath(finalFolder, Path.Combine(finalFolder, "VOD", "vod_chat.json"));
              LogInformation("Downloaded VOD chat", downloadLogger);

              if ((!streamer.OverrideRenderSettings && globalSettings.RenderSettings.RenderChat && (globalSettings.RenderSettings.RenderPrefrence == RenderPrefrence.VOD || globalSettings.RenderSettings.RenderPrefrence == RenderPrefrence.Both)) || (streamer.OverrideRenderSettings && streamer.RenderSettings.RenderChat && (streamer.RenderSettings.RenderPrefrence == RenderPrefrence.VOD || streamer.RenderSettings.RenderPrefrence == RenderPrefrence.Both)))
              {
                try
                {
                  RenderSettings renderSettings = streamer.OverrideRenderSettings ? streamer.RenderSettings : globalSettings.RenderSettings;
                  ChatRenderOptions renderOptions = new ChatRenderOptions();
                  renderOptions.InputFile = Path.Combine(downloadFolder, "VOD", "vod_chat.json");
                  renderOptions.OutputFile = Path.Combine(downloadFolder, "VOD", "vod_chat." + renderSettings.VideoContainer.ToLower());
                  renderOptions.BackgroundColor = SKColor.Parse(renderSettings.BackgroundColor);
                  renderOptions.MessageColor = SKColor.Parse(renderSettings.FontColor);
                  renderOptions.ChatHeight = renderSettings.Height;
                  renderOptions.ChatWidth = renderSettings.Width;
                  renderOptions.BttvEmotes = renderSettings.BTTVEmotes;
                  renderOptions.FfzEmotes = renderSettings.FFZEmotes;
                  renderOptions.StvEmotes = renderSettings.STVEmotes;
                  renderOptions.Outline = renderSettings.Outline;
                  renderOptions.OutlineSize = 4;
                  renderOptions.Font = renderSettings.Font;
                  renderOptions.FontSize = renderSettings.FontSize;
                  renderOptions.MessageFontStyle = SKFontStyle.Normal;
                  renderOptions.UsernameFontStyle = SKFontStyle.Bold;
                  renderOptions.UpdateRate = renderSettings.UpdateTime;
                  renderOptions.PaddingLeft = 2;
                  renderOptions.Framerate = renderSettings.Framerate;
                  renderOptions.GenerateMask = renderSettings.GenerateMask;
                  renderOptions.InputArgs = renderSettings.InputArgs;
                  renderOptions.OutputArgs = renderSettings.OutputArgs;
                  renderOptions.FfmpegPath = Path.GetFullPath("ffmpeg");
                  renderOptions.TempFolder = Settings.Settings.Default.TempFolder;
                  renderOptions.SubMessages = renderSettings.SubMessages;

                  ChatRenderer chatRenderer = new ChatRenderer(renderOptions);
                  chatRenderer.RenderVideoAsync(new Progress<ProgressReport>(), new CancellationToken()).Wait();
                }
                catch (Exception ex)
                {
                  LogError(ex, "Chat render error: " + ex.Message, downloadLogger);
                }
              }
            }
            catch (Exception ex)
            {
              LogError(ex, "Error downloading VOD chat: " + ex.Message, downloadLogger);
            }
          });
          renderTasks.Add(t);
        }

        try
        {
          if (liveTask != null)
            liveTask.Wait();
          streamMetadata.LiveStreamPath = Path.GetRelativePath(finalFolder, Path.Combine(finalFolder, "Live", "live.mp4"));
          if (File.Exists(streamMetadata.LiveStreamPath))
          {
            ShellFile shellFile = ShellFile.FromFilePath(Path.Combine(downloadFolder, "Live", "live.mp4"));
            if (streamMetadata.Thumbnail == null)
            {
              using MemoryStream ms = new MemoryStream();
              shellFile.Thumbnail.LargeBitmap.Save(ms, ImageFormat.Png);
              streamMetadata.Thumbnail = Convert.ToBase64String(ms.ToArray());
            }
            long? timeTicks = (long?)shellFile.Properties.System.Media.Duration.Value;
            if (timeTicks != null)
            {
              //Live could be longer than VOD or reverse, so set length to longer one
              int videoLength = (int)TimeSpan.FromTicks((long)timeTicks).TotalSeconds;
              if (videoLength > streamMetadata.Length)
                streamMetadata.Length = videoLength;
            }
          }
          else
          {
            LogInformation($"LiveTask {streamId} - live.mp4 does not exist", downloadLogger);
          }
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in DownloadTask near LiveTask", downloadLogger);
        }

        try
        {
          if (liveChatTask != null)
            liveChatTask.Wait();
          streamMetadata.LiveChatPath = Path.GetRelativePath(finalFolder, Path.Combine(finalFolder, "Live", "live_chat.json"));
          if ((!streamer.OverrideRenderSettings && globalSettings.RenderSettings.RenderChat && (globalSettings.RenderSettings.RenderPrefrence == RenderPrefrence.Live || globalSettings.RenderSettings.RenderPrefrence == RenderPrefrence.Both)) || (streamer.OverrideRenderSettings && streamer.RenderSettings.RenderChat && (streamer.RenderSettings.RenderPrefrence == RenderPrefrence.Live || streamer.RenderSettings.RenderPrefrence == RenderPrefrence.Both)))
          {
            Task t = Task.Run(() =>
            {
              try
              {
                RenderSettings renderSettings = streamer.OverrideRenderSettings ? streamer.RenderSettings : globalSettings.RenderSettings;
                ChatRenderOptions renderOptions = new ChatRenderOptions();
                renderOptions.InputFile = Path.Combine(downloadFolder, "Live", "live_chat.json");
                renderOptions.OutputFile = Path.Combine(downloadFolder, "Live", "live_chat." + renderSettings.VideoContainer.ToLower());
                renderOptions.BackgroundColor = SKColor.Parse(renderSettings.BackgroundColor);
                renderOptions.MessageColor = SKColor.Parse(renderSettings.FontColor);
                renderOptions.ChatHeight = renderSettings.Height;
                renderOptions.ChatWidth = renderSettings.Width;
                renderOptions.BttvEmotes = renderSettings.BTTVEmotes;
                renderOptions.FfzEmotes = renderSettings.FFZEmotes;
                renderOptions.StvEmotes = renderSettings.STVEmotes;
                renderOptions.Outline = renderSettings.Outline;
                renderOptions.OutlineSize = 4;
                renderOptions.Font = renderSettings.Font;
                renderOptions.FontSize = renderSettings.FontSize;
                renderOptions.MessageFontStyle = SKFontStyle.Normal;
                renderOptions.UsernameFontStyle = SKFontStyle.Bold;
                renderOptions.UpdateRate = renderSettings.UpdateTime;
                renderOptions.PaddingLeft = 2;
                renderOptions.Framerate = renderSettings.Framerate;
                renderOptions.GenerateMask = renderSettings.GenerateMask;
                renderOptions.InputArgs = renderSettings.InputArgs;
                renderOptions.OutputArgs = renderSettings.OutputArgs;
                renderOptions.FfmpegPath = Path.GetFullPath("ffmpeg");
                renderOptions.TempFolder = Settings.Settings.Default.TempFolder;
                renderOptions.SubMessages = renderSettings.SubMessages;

                ChatRenderer chatRenderer = new ChatRenderer(renderOptions);
                chatRenderer.RenderVideoAsync(new Progress<ProgressReport>(), new CancellationToken()).Wait();
              }
              catch (Exception ex)
              {
                LogError(ex, "Live chat render error: " + ex.Message, downloadLogger);
                downloadLogger.Error("Live chat render error: " + ex);
              }
            });
            renderTasks.Add(t);
          }
        }
        catch (Exception ex)
        {
          LogError(ex, "Live chat error: " + liveChatTask?.Exception ?? "LiveChatTask NULL", downloadLogger);
          if (liveChatTask != null && liveChatTask.IsFaulted && liveChatTask.Exception != null)
          {
            downloadLogger.Error("Live chat error: " + liveChatTask.Exception);
          }
        }

        infoCancel.Cancel();
        try
        {
          if (infoTask != null)
          {
            infoTask.Wait();
            if (infoTask.IsCompletedSuccessfully)
            {
              StreamInfoTaskResponse data = infoTask.Result;
              streamMetadata.Games = data.Games;
              streamMetadata.Title = data.Title;
            }
          }
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in DownloadTask near InfoTask", downloadLogger);
        }

        File.WriteAllText(Path.Combine(downloadFolder, "metadata.json"), JsonConvert.SerializeObject(streamMetadata));

        try
        {
          Task.WaitAll(renderTasks.ToArray());
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in DownloadTask near RenderTasks", downloadLogger);
        }

        LogInformation("Finished downloading", downloadLogger);

        if (!Directory.Exists(finalFolder))
          Directory.CreateDirectory(finalFolder);

        DirectoryCopy(downloadFolder, finalFolder, true);

        Directory.Delete(downloadFolder, true);
        try
        {
          Streamer? gridStreamer = PageStreamers.GridItems.FirstOrDefault(x => x.Id == streamer.Id);
          if (gridStreamer != null)
            gridStreamer.StreamCount++;
          PageVods.RefreshVods();
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in DownloadTask near RefreshVods", downloadLogger);
        }
      }
      catch (Exception ex)
      {
        LogError(ex, "Fatal error downloading stream: " + ex.ToString(), downloadLogger);
      }
      finally
      {
        downloadLogger.Dispose();
      }
    }

    private StreamInfoTaskResponse DownloadInfoTask(Streamer streamer, CancellationToken token, ILogger downloadLogger)
    {
      LogDebug("DownloadInfoTask STARTED", downloadLogger);
      StreamInfoTaskResponse resData = new StreamInfoTaskResponse();
      while (!token.IsCancellationRequested)
      {
        using WebClient apiClient = new WebClient();
        apiClient.Headers["Content-Type"] = "application/json";
        apiClient.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
        StreamGqlData? streamInfo = JsonConvert.DeserializeObject<StreamGqlData>(apiClient.UploadString("https://gql.twitch.tv/gql", "{\"query\":\"query{user(id:\\\"" + streamer.Id + "\\\"){id,login,displayName,broadcastSettings{title},stream{archiveVideo{id}id,title,type,viewersCount,createdAt,game{name}}}}\",\"variables\":{}}"));

        if (streamInfo != null)
        {
          if (streamInfo.data != null && streamInfo.data.user != null)
          {
            if (streamInfo.data.user.stream != null)
            {
              if (resData.Title == null && streamInfo.data.user.stream.title != null)
              {
                resData.Title = streamInfo.data.user.stream.title;
                LogDebug($"DownloadInfoTask Setting ResData.Title to Stream Title: {resData.Title}", downloadLogger);
              }
              if (streamInfo.data.user.stream.game != null && streamInfo.data.user.stream.game.name != null)
              {
                string game = streamInfo.data.user.stream.game.name!;
                LogDebug($"DownloadInfoTask found stream game {game}", downloadLogger);
                if (!resData.Games.Contains(game))
                {
                  LogDebug("DownloadInfoTask Adding stream game to ResData", downloadLogger);
                  resData.Games.Add(game);
                }
              }
            }

            if (resData.Title == null && streamInfo.data.user.broadcastSettings != null && streamInfo.data.user.broadcastSettings.title != null)
            {
              resData.Title = streamInfo.data.user.broadcastSettings.title;
              LogDebug($"DownloadInfoTask Setting ResData.Title to Broadcast Title: {resData.Title}", downloadLogger);
            }
          }
        }
        else
        {
          LogDebug($"DownloadInfoTask StreamInfo was null", downloadLogger);
        }

        LogDebug("DownloadInfoTask Sleep", downloadLogger);
        Thread.Sleep(60000);
      }

      LogDebug("DownloadInfoTask ENDED", downloadLogger);
      return resData;
    }

    private void DownloadLiveChatTask(Streamer streamer, string liveDirectory, string playlistUrl, string? firstPlaylist, DateTime startTime, CancellationToken token, ILogger downloadLogger)
    {
      TwitchClient client = new TwitchClient();
      ConnectionCredentials credentials = new ConnectionCredentials("justinfan123456", "");
      long start = DateTimeOffset.Now.ToUnixTimeSeconds();
      client.Initialize(credentials, streamer.Name);
      List<Comment> messages = new List<Comment>();

      client.OnMessageReceived += (object? sender, TwitchLib.Client.Events.OnMessageReceivedArgs e) =>
      {
        Comment? newMessage = GetComment(e.ChatMessage, streamer, start);
        if (newMessage != null)
          messages.Add(newMessage);
      };

      client.OnConnectionError += (object? sender, TwitchLib.Client.Events.OnConnectionErrorArgs e) =>
      {
        client.Connect();
        client.JoinChannel(streamer.Name);
      };

      //Attempt to sync start of live chat with first segment of live recording
      if (firstPlaylist != null)
      {
        try
        {
          string timeLine = firstPlaylist.Split('\n').First(x => x.Contains("#EXT-X-PROGRAM-DATE-TIME:"));
          start = DateTimeOffset.Parse(timeLine.Substring("#EXT-X-PROGRAM-DATE-TIME:".Length)).ToUnixTimeSeconds();
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in DownloadLiveChatTask when setting up headers", downloadLogger);
        }
      }

      client.Connect();

      bool isLive = true;

      while (isLive)
      {
        try
        {
          new WebClient().DownloadString(playlistUrl);
        }
        catch (WebException ex)
        {
          HttpWebResponse? res = (HttpWebResponse?)ex.Response;
          if (res != null && res.StatusCode == HttpStatusCode.NotFound)
            isLive = false;
        }
        Thread.Sleep(15000);
      }
      client.Disconnect();

      ChatRoot chatRoot = new ChatRoot();
      chatRoot.comments = messages;
      chatRoot.streamer = new global::Streamer();
      chatRoot.streamer.name = streamer.Name;
      chatRoot.streamer.id = int.Parse(streamer.Id);
      chatRoot.video = new VideoTime();
      chatRoot.video.start = 0;
      chatRoot.video.end = (int)messages.Last().content_offset_seconds;

      File.WriteAllText(Path.Combine(liveDirectory, "live_chat.json"), JsonConvert.SerializeObject(chatRoot), Encoding.Unicode);
    }

    public static Comment? GetComment(ChatMessage chatMessage, Streamer streamer, long startTime)
    {
      Comment returnComment = new Comment();

      long commentTime = long.Parse(chatMessage.TmiSentTs);
      returnComment._id = chatMessage.Id;
      returnComment.created_at = DateTimeOffset.FromUnixTimeMilliseconds(commentTime).UtcDateTime;
      returnComment.updated_at = returnComment.created_at;
      returnComment.channel_id = streamer.Id;
      returnComment.content_type = "live";
      returnComment.content_id = "";
      returnComment.content_offset_seconds = (commentTime / 1000.0) - startTime;
      returnComment.commenter = new Commenter();
      returnComment.commenter.display_name = chatMessage.DisplayName;
      returnComment.commenter._id = chatMessage.UserId;
      returnComment.commenter.name = chatMessage.Username;
      returnComment.commenter.type = "user";
      returnComment.source = "chat";
      returnComment.state = "published";

      if (returnComment.content_offset_seconds < 0)
        return null;

      returnComment.message = new Message();
      if (chatMessage.IsMe)
        returnComment.message.is_action = true;
      else
        returnComment.message.is_action = false;

      returnComment.message.body = chatMessage.Message;
      returnComment.message.emoticons = new List<Emoticon2>();
      foreach (var currentEmote in chatMessage.EmoteSet.Emotes)
      {
        Emoticon2 newEmote = new Emoticon2();
        newEmote._id = currentEmote.Id;
        newEmote.begin = currentEmote.StartIndex;
        newEmote.end = currentEmote.EndIndex;
        returnComment.message.emoticons.Add(newEmote);
      }

      returnComment.message.fragments = new List<Fragment>();
      Fragment currentFragment = new Fragment();
      currentFragment.text = "";

      for (int i = 0; i < chatMessage.Message.Length; i++)
      {
        foreach (var currentEmote in returnComment.message.emoticons)
        {
          if (i == currentEmote.begin)
          {
            if (currentFragment.text != "")
            {
              returnComment.message.fragments.Add(currentFragment);
              currentFragment = new Fragment();
              currentFragment.text = "";
            }

            if (currentFragment.emoticon == null)
              currentFragment.emoticon = new Emoticon();
            currentFragment.emoticon.emoticon_id = currentEmote._id;
            currentFragment.emoticon.emoticon_set_id = "";
          }
        }
        currentFragment.text += chatMessage.Message[i];
        foreach (var currentEmote in returnComment.message.emoticons)
        {
          if (i == currentEmote.end)
          {
            returnComment.message.fragments.Add(currentFragment);
            currentFragment = new Fragment();
            currentFragment.text = "";
          }
        }
      }

      if (currentFragment.text != "")
        returnComment.message.fragments.Add(currentFragment);
      returnComment.message.user_badges = new List<UserBadge>();

      foreach (var currentBadge in chatMessage.Badges)
      {
        UserBadge newBadge = new UserBadge();
        newBadge._id = currentBadge.Key;
        newBadge.version = currentBadge.Value;
        returnComment.message.user_badges.Add(newBadge);
      }
      returnComment.message.user_color = (chatMessage.ColorHex == "" ? null : chatMessage.ColorHex);
      returnComment.message.user_notice_params = new UserNoticeParams();

      return returnComment;
    }

    //https://docs.microsoft.com/en-us/dotnet/standard/io/how-to-copy-directories
    private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
    {
      // Get the subdirectories for the specified directory.
      DirectoryInfo dir = new DirectoryInfo(sourceDirName);

      if (!dir.Exists)
      {
        throw new DirectoryNotFoundException(
            "Source directory does not exist or could not be found: "
            + sourceDirName);
      }

      DirectoryInfo[] dirs = dir.GetDirectories();

      // If the destination directory doesn't exist, create it.       
      Directory.CreateDirectory(destDirName);

      // Get the files in the directory and copy them to the new location.
      FileInfo[] files = dir.GetFiles();
      foreach (FileInfo file in files)
      {
        string tempPath = Path.Combine(destDirName, file.Name);
        file.CopyTo(tempPath, false);
      }

      // If copying subdirectories, copy them and their contents to new location.
      if (copySubDirs)
      {
        foreach (DirectoryInfo subdir in dirs)
        {
          string tempPath = Path.Combine(destDirName, subdir.Name);
          DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
        }
      }
    }

    private string? GetVodPlaylistUrl(string vodId, Streamer streamer, GlobalSettings globalSettings, ILogger logger)
    {
      int tryCount = 0;
      while (tryCount < 20)
      {
        try
        {
          using WebClient client = new WebClient();
          client.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");
          if (streamer.OverrideRecordingSettings && streamer.RecordingSettings.EnableVodOauth)
            client.Headers.Add("Authorization", "OAuth " + streamer.RecordingSettings.VodOauth);
          else if (!streamer.OverrideRecordingSettings && globalSettings.RecordingSettings.EnableVodOauth)
            client.Headers.Add("Authorization", "OAuth " + globalSettings.RecordingSettings.VodOauth);
          TokenGQLResponse? response = JsonConvert.DeserializeObject<TokenGQLResponse>(client.UploadString("https://gql.twitch.tv/gql", "{\"operationName\":\"PlaybackAccessToken\",\"variables\":{\"isLive\":false,\"login\":\"\",\"isVod\":true,\"vodID\":\"" + vodId + "\",\"playerType\":\"channel_home_live\"},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712\"}}}"));
          if (response != null && response.data != null && response.data.videoPlaybackAccessToken != null && response.data.videoPlaybackAccessToken.signature != null && response.data.videoPlaybackAccessToken.value != null)
          {
            string[] playlistParts = client.DownloadString(String.Format("http://usher.twitch.tv/vod/{0}?nauth={1}&nauthsig={2}&allow_source=true&player=twitchweb", vodId, response.data.videoPlaybackAccessToken.value, response.data.videoPlaybackAccessToken.signature)).Split("\n");
            for (int i = 0; i < playlistParts.Length; i++)
            {
              if (playlistParts[i].Contains("#EXT-X-MEDIA"))
              {
                string lastPart = playlistParts[i].Substring(playlistParts[i].IndexOf("NAME=\"") + 6);
                string stringQuality = lastPart.Substring(0, lastPart.IndexOf("\""));

                return playlistParts[i + 2];
              }
            }
          }
        }
        catch (Exception ex)
        {
          LogError(ex, "Error in GetVodPlaylistUrl", logger);
        }
        tryCount++;
      }
      return null;
    }

    private void DownloadVodTask(string? playlistUrl, string vodDirectory, string vodId, CancellationToken token, ILogger downloadLogger)
    {
      bool isLive = true;
      string baseUrl = "";
      if (playlistUrl != null)
        baseUrl = playlistUrl.Substring(0, playlistUrl.LastIndexOf("/") + 1);
      int failCount = 0;

      while (isLive && !token.IsCancellationRequested)
      {
        try
        {
          using WebClient gqlClient = new WebClient();
          gqlClient.Headers["Content-Type"] = "application/json";
          gqlClient.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");
          VodGQLResponse? response = JsonConvert.DeserializeObject<VodGQLResponse>(gqlClient.UploadString("https://gql.twitch.tv/gql", "{\"query\":\"query{video(id:" + vodId + "){id,status,broadcastType,createdAt,game{name},title,publishedAt,recordedAt}}\",\"variables\":{}}"));
          if (response != null)
          {
            if (response.data != null && response.data.video != null && response.data.video.status != null)
            {
              if (response.data.video.status == "RECORDED")
                isLive = false;
            }
            else if (response.data != null && response.data.video == null)
            {
              //Assumed not live if failure to get new VOD part after 10 minutes
              if (failCount > 60)
              {
                isLive = false;
              }
            }
          }
        }
        catch (WebException ex)
        {
          HttpWebResponse? res = (HttpWebResponse?)ex.Response;
          if (res == null || res.StatusCode != HttpStatusCode.NotFound)
            LogError(ex, "WebException in DownloadVodTask", downloadLogger);
        }
        catch (Exception ex)
        {
          LogError(ex, "Exception in DownloadVodTask", downloadLogger);
        }

        using WebClient client = new WebClient();
        try
        {
          if (playlistUrl != null)
          {
            List<string> downloadParts = new List<string>(client.DownloadString(playlistUrl).Split('\n').Where(x => !x.StartsWith('#')));
            foreach (var url in downloadParts)
            {
              string filePath = Path.Combine(vodDirectory, url);
              if (!File.Exists(filePath))
                new WebClient().DownloadFile(baseUrl + url, filePath);
            }
          }
        }
        catch
        {
          //VOD probably deleted, so playlist is gone
          try
          {
            int latestChunk = Directory.GetFiles(vodDirectory, "*.ts").Select(x => Int32.Parse(x.Substring(0, x.Length - 3))).OrderBy(v => v).Last() + 1;
            bool doneDownloading = false;
            while (!doneDownloading)
            {
              string filePath = Path.Combine(vodDirectory, latestChunk + ".ts");
              try
              {
                if (!File.Exists(filePath))
                  new WebClient().DownloadFile(baseUrl + latestChunk + ".ts", filePath);
                failCount = 0;
              }
              catch
              {
                failCount++;
                doneDownloading = true;
              }
            }
          }
          catch (Exception ex)
          {
            LogError(ex, "Error in DownloadVodTask in VOD Deleted section", downloadLogger);
          }
        }

        Thread.Sleep(10000);
      }

      if (playlistUrl == null)
        return;

      List<string> videoPartsList = new List<string>(Directory.GetFiles(vodDirectory, "*.ts"));
      videoPartsList.Sort(FileNameCompare);
      string outputFile = Path.Combine(vodDirectory, "vod.ts");

      using (FileStream outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
      {
        foreach (var file in videoPartsList)
        {
          if (File.Exists(file))
          {
            byte[] writeBytes = File.ReadAllBytes(file);
            outputStream.Write(writeBytes, 0, writeBytes.Length);

            try
            {
              File.Delete(file);
            }
            catch (Exception ex)
            {
              LogError(ex, $"Error deleting file {file}", downloadLogger);
            }
          }
        }
      }

      try
      {
        var process = new Process
        {
          StartInfo =
                    {
                        FileName = Path.GetFullPath("ffmpeg"),
                        Arguments = String.Format("-y -avoid_negative_ts make_zero -i \"{0}\" -analyzeduration {1} -probesize {1} -c copy \"{2}\"", Path.Combine(vodDirectory, "vod.ts"), Int32.MaxValue, Path.Combine(vodDirectory, "vod.mp4")),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    }
        };
        process.Start();
        process.WaitForExit();
        if (File.Exists(outputFile))
          File.Delete(outputFile);
      }
      catch (Exception ex)
      {
        LogError(ex, "Error in DownloadVodTask", downloadLogger);
      }
    }
    private static int FileNameCompare(string x, string y)
    {
      x = x.Replace("-unmuted", "").Replace("-muted", "");
      y = y.Replace("-unmuted", "").Replace("-muted", "");
      int x_int = Int32.Parse(Path.GetFileNameWithoutExtension(x));
      int y_int = Int32.Parse(Path.GetFileNameWithoutExtension(y));

      if (x_int < y_int)
        return -1;
      else if (x_int > y_int)
        return 1;
      else
        return 0;
    }

    private static int FileNameCompareLive(string x, string y)
    {
      long x_int = long.Parse(Path.GetFileNameWithoutExtension(x).Split('-')[0]);
      long y_int = long.Parse(Path.GetFileNameWithoutExtension(y).Split('-')[0]);

      if (x_int < y_int)
        return -1;
      else if (x_int > y_int)
        return 1;
      else
        return 0;
    }

    private void DownloadLiveTask(string playlistUrl, string? firstPlaylist, List<string> livePartsList, string liveDirectory, Logger logger, CancellationToken token, ILogger downloadLogger)
    {
      bool isLive = true;
      while (isLive && !token.IsCancellationRequested)
      {
        try
        {
          if (firstPlaylist != null)
          {
            firstPlaylist = null;
            DownloadLiveParts(playlistUrl, firstPlaylist, livePartsList, liveDirectory);
          }
          else
          {
            DownloadLiveParts(playlistUrl, null, livePartsList, liveDirectory);
          }
        }
        catch (WebException ex)
        {
          HttpWebResponse? res = (HttpWebResponse?)ex.Response;
          if (res != null && res.StatusCode == HttpStatusCode.NotFound)
          {
            LogInformation("Detected streamer offline", downloadLogger);
            isLive = false;
          }
          else
          {
            LogError(ex, "WebException in DownloadLiveTask", downloadLogger);
          }
        }
        Thread.Sleep(1000);
      }
      List<string> liveList = new List<string>(Directory.GetFiles(liveDirectory, "*.ts"));
      liveList.Sort(FileNameCompareLive);
      string liveOutputFile = Path.Combine(liveDirectory, "live.ts");

      try
      {
        using (FileStream outputStream = new FileStream(liveOutputFile, FileMode.Create, FileAccess.Write))
        {
          foreach (var file in liveList)
          {
            if (File.Exists(file))
            {
              byte[] writeBytes = File.ReadAllBytes(file);
              outputStream.Write(writeBytes, 0, writeBytes.Length);

              try
              {
                File.Delete(file);
              }
              catch (Exception ex)
              {
                LogError(ex, $"Error deleting file {file}", downloadLogger);
              }
            }
          }
        }

        var process = new Process
        {
          StartInfo =
                    {
                        FileName = Path.GetFullPath("ffmpeg"),
                        Arguments = String.Format("-y -avoid_negative_ts make_zero -i \"{0}\" -analyzeduration {1} -probesize {1} -c copy \"{2}\"", Path.Combine(liveDirectory, "live.ts"), Int32.MaxValue, Path.Combine(liveDirectory, "live.mp4")),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    }
        };
        process.Start();
        process.WaitForExit();
        if (File.Exists(liveOutputFile))
          File.Delete(liveOutputFile);
      }
      catch (Exception ex)
      {
        LogError(ex, "Error combining/remuxing live video file: " + ex?.Message, downloadLogger);
      }
    }

    public static void DownloadLiveParts(string livePlaylist, string? firstPlaylistResult, List<string> livePartsList, string liveDirectory)
    {
      using WebClient client = new WebClient();
      List<string> videoParts = (firstPlaylistResult == null ? client.DownloadString(livePlaylist) : firstPlaylistResult).Split('\n').ToList();
      for (int i = 0; i < videoParts.Count; i++)
      {
        if (videoParts[i].Contains("#EXT-X-PROGRAM-DATE-TIME:"))
        {
          DateTimeOffset segmentTime = DateTimeOffset.Parse(videoParts[i].Substring("#EXT-X-PROGRAM-DATE-TIME:".Length));
          double segmentLength = double.Parse(videoParts[i + 1].Substring("#EXTINF:".Length).Split(',')[0]);
          string videoUrl = videoParts[i + 2];
          if (!livePartsList.Contains(videoUrl))
          {
            client.DownloadFile(videoUrl, Path.Combine(liveDirectory, segmentTime.ToUnixTimeMilliseconds() + "-" + segmentLength + ".ts"));
            livePartsList.Add(videoUrl);
          }
        }
      }
    }

    private bool RefreshToken(Streamer streamer, Dictionary<string, AccessToken> accessTokens, GlobalSettings globalSettings)
    {
      try
      {
        using WebClient client = new WebClient();
        client.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
        if (streamer.OverrideRecordingSettings && streamer.RecordingSettings.EnableLiveOauth)
          client.Headers.Add("Authorization", "OAuth " + streamer.RecordingSettings.LiveOauth);
        else if (!streamer.OverrideRecordingSettings && globalSettings.RecordingSettings.EnableLiveOauth)
          client.Headers.Add("Authorization", "OAuth " + globalSettings.RecordingSettings.LiveOauth);
        else
          client.Headers.Add("Authorization", "undefined");
        string res = client.UploadString("https://gql.twitch.tv/gql", "{\"operationName\":\"PlaybackAccessToken_Template\",\"query\":\"query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) {  streamPlaybackAccessToken(channelName: $login, params: {platform: \\\"web\\\", playerBackend: \\\"mediaplayer\\\", playerType: $playerType}) @include(if: $isLive) {    value    signature    __typename  }  videoPlaybackAccessToken(id: $vodID, params: {platform: \\\"web\\\", playerBackend: \\\"mediaplayer\\\", playerType: $playerType}) @include(if: $isVod) {    value    signature    __typename  }}\",\"variables\":{\"isLive\":true,\"login\":\"" + streamer.Name.ToLower() + "\",\"isVod\":false,\"vodID\":\"\",\"playerType\":\"site\"}}");
        TokenGQLResponse? response = JsonConvert.DeserializeObject<TokenGQLResponse>(res);

        if (response != null && response.data != null && response.data.streamPlaybackAccessToken != null && response.data.streamPlaybackAccessToken.value != null)
        {
          AccessTokenValue? tokenValue = JsonConvert.DeserializeObject<AccessTokenValue>(response.data.streamPlaybackAccessToken.value);
          if (tokenValue != null && tokenValue.expires != null && response.data.streamPlaybackAccessToken.signature != null)
          {
            if (!accessTokens.ContainsKey(streamer.Id))
              accessTokens[streamer.Id] = new AccessToken();

            accessTokens[streamer.Id].value = response.data.streamPlaybackAccessToken.value;
            accessTokens[streamer.Id].signature = response.data.streamPlaybackAccessToken.signature;
            accessTokens[streamer.Id].expires = DateTimeOffset.FromUnixTimeSeconds((long)tokenValue.expires).DateTime.ToLocalTime();
            return true;
          }
        }
      }
      catch (WebException ex)
      {
        HttpWebResponse? res = (HttpWebResponse?)ex.Response;
        if (res == null || res.StatusCode != HttpStatusCode.NotFound)
          LogError(ex, "WebException in RefreshToken");
      }
      return false;
    }

    public void LogInformation(string message, ILogger logger = null)
    {
      string logMessage = $"ThreadId {Thread.CurrentThread.ManagedThreadId} - {message}";
      Trace.WriteLine($"{DateTime.Now.ToString(logDatePattern)} [INF] " + logMessage);
      (logger ?? mainLogger)?.Information(logMessage);
    }

    public void LogError(Exception ex, string message, ILogger logger = null)
    {
      string logMessage = $"ThreadId {Thread.CurrentThread.ManagedThreadId} - {message}";
      Trace.WriteLine($"{DateTime.Now.ToString(logDatePattern)} [ERR] " + logMessage);
      Trace.WriteLine(ex);
      (logger ?? mainLogger)?.Error(logMessage);
      (logger ?? mainLogger)?.Error(ex.ToString());
    }

    [Conditional("DEBUG")]
    public void LogDebug(string message, ILogger logger = null)
    {
      string logMessage = $"ThreadId {Thread.CurrentThread.ManagedThreadId} - {message}";
      Trace.WriteLine($"{DateTime.Now.ToString(logDatePattern)} [DBG] " + logMessage);
      (logger ?? mainLogger)?.Debug(logMessage);
    }

    [Conditional("VERBOSE")]
    public void LogVerbose(string message, ILogger logger = null)
    {
      string logMessage = $"ThreadId {Thread.CurrentThread.ManagedThreadId} - {message}";
      Trace.WriteLine($"{DateTime.Now.ToString(logDatePattern)} [VRB] " + logMessage);
      (logger ?? mainLogger)?.Verbose(logMessage);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
      Main.Content = pageStreamers;
    }

    private void SideMenuItem_Selected(object sender, RoutedEventArgs e)
    {
      Main.Content = pageStreamers;
    }

    private void SideMenuItem_Selected_1(object sender, RoutedEventArgs e)
    {
      Main.Content = pageVods;
    }

    private void SideMenuItem_Selected_2(object sender, RoutedEventArgs e)
    {
      Main.Content = pageSettings;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
      this.Hide();
      NotifyIcon.Visibility = Visibility.Visible;
      ShowInTaskbar = false;
      e.Cancel = true;
    }

    private void NotifyIcon_MouseDoubleClick(object sender, RoutedEventArgs e)
    {
      this.Show();
      NotifyIcon.Visibility = Visibility.Hidden;
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
      this.Show();
      NotifyIcon.Visibility = Visibility.Hidden;
    }
  }

  public class Broadcast
  {
    public string StreamId { get; set; }
    public string StreamerId { get; set; } // TODO: Stop and remove active broadcasts if a streamer is removed from the list
    public Task DownloadTask { get; set; }
    public Broadcast(string streamId, string streamerId, Task downloadTask)
    {
      StreamId = streamId;
      StreamerId = streamerId;
      DownloadTask = downloadTask;
    }
  }
}