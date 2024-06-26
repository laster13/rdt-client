using System;
using System.Net;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Aria2NET;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.Downloaders;
using Newtonsoft.Json.Linq;

namespace RdtClient.Service.Services;

public class TorrentRunner
{
    public static readonly ConcurrentDictionary<Guid, DownloadClient> ActiveDownloadClients = new();
    public static readonly ConcurrentDictionary<Guid, UnpackClient> ActiveUnpackClients = new();

    private readonly ILogger<TorrentRunner> _logger;
    private readonly Torrents _torrents;
    private readonly Downloads _downloads;
    private readonly RemoteService _remoteService;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<Guid, string> _aggregatedDownloadResults;
    private readonly Dictionary<Guid, string> _aggregatedDownloadErrors;

    public TorrentRunner(ILogger<TorrentRunner> logger, Torrents torrents, Downloads downloads, RemoteService remoteService)
    {
        _logger = logger;
        _torrents = torrents;
        _downloads = downloads;
        _remoteService = remoteService;
        _aggregatedDownloadResults = new Dictionary<Guid, string>();
        _aggregatedDownloadErrors = new Dictionary<Guid, string>();

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task Initialize()
    {
        Log("Initializing TorrentRunner");

        var settingsCopy = JsonSerializer.Deserialize<DbSettings>(JsonSerializer.Serialize(Settings.Get));

        if (settingsCopy != null)
        {
            settingsCopy.Provider.ApiKey = "*****";
            settingsCopy.DownloadClient.Aria2cSecret = "*****";

            Log(JsonSerializer.Serialize(settingsCopy));
        }

        // When starting up reset any pending downloads or unpackings so that they are restarted.
        var torrents = await _torrents.Get();
            
        torrents = torrents.Where(m => m.Completed == null).ToList();

        Log($"Found {torrents.Count} not completed torrents");

        foreach (var torrent in torrents)
        {
            foreach (var download in torrent.Downloads)
            {
                if (download.DownloadQueued != null && download.DownloadStarted != null && download.DownloadFinished == null && download.Error == null)
                {
                    Log("Resetting download status", download, torrent);

                    await _downloads.UpdateDownloadStarted(download.DownloadId, null);
                }

                if (download.UnpackingQueued != null && download.UnpackingStarted != null && download.UnpackingFinished == null && download.Error == null)
                {
                    Log("Resetting unpack status", download, torrent);

                    await _downloads.UpdateUnpackingStarted(download.DownloadId, null);
                }
            }
        }

        Log("TorrentRunner Initialized");
    }

    public async Task Tick()
    {
        if (String.IsNullOrWhiteSpace(Settings.Get.Provider.ApiKey))
        {
            Log($"No RealDebridApiKey set in settings");
            return;
        }

        if (Settings.Get.DownloadClient.Client == Data.Enums.DownloadClient.Symlink)
        {
            var rcloneMountPath = Settings.Get.DownloadClient.RcloneMountPath;

            if (!Directory.Exists(rcloneMountPath))
            {
                Log($"Rclone mount path ({rcloneMountPath}) was not found!");
                return;
            }
        }

        var settingDownloadLimit = Settings.Get.General.DownloadLimit;
        if (settingDownloadLimit < 1)
        {
            settingDownloadLimit = 1;
        }

        var settingUnpackLimit = Settings.Get.General.UnpackLimit;
        if (settingUnpackLimit < 1)
        {
            settingUnpackLimit = 1;
        }

        var settingDownloadPath = Settings.Get.DownloadClient.DownloadPath;
        if (String.IsNullOrWhiteSpace(settingDownloadPath))
        {
            _logger.LogError("No DownloadPath set in settings");
            return;
        }

        var sw = new Stopwatch();
        sw.Start();

        if (!ActiveDownloadClients.IsEmpty || !ActiveUnpackClients.IsEmpty)
        {
            Log($"TorrentRunner Tick Start, {ActiveDownloadClients.Count} active downloads, {ActiveUnpackClients.Count} active unpacks");
        }

        if (ActiveDownloadClients.Any(m => m.Value.Type == Data.Enums.DownloadClient.Aria2c))
        {
            Log("Updating Aria2 status");

            var aria2NetClient = new Aria2NetClient(Settings.Get.DownloadClient.Aria2cUrl, Settings.Get.DownloadClient.Aria2cSecret, _httpClient, 1);

            var allDownloads = await aria2NetClient.TellAllAsync();

            Log($"Found {allDownloads.Count} Aria2 downloads");

            foreach (var activeDownload in ActiveDownloadClients)
            {
                if (activeDownload.Value.Downloader is Aria2cDownloader aria2Downloader)
                {
                    await aria2Downloader.Update(allDownloads);
                }
            }

            Log("Finished updating Aria2 status");
        }

        // Check if any torrents are finished downloading to the host, remove them from the active download list.
        var completedActiveDownloads = ActiveDownloadClients.Where(m => m.Value.Finished).ToList();

        if (completedActiveDownloads.Count > 0)
        {
            Log($"Processing {completedActiveDownloads.Count} completed downloads");

            foreach (var (downloadId, downloadClient) in completedActiveDownloads)
            {
                var download = await _downloads.GetById(downloadId);

                if (download == null)
                {
                    ActiveDownloadClients.TryRemove(downloadId, out _);

                    Log($"Download with ID {downloadId} not found! Removed from download queue");

                    continue;
                }

                Log("Processing download", download, download.Torrent);

                if (!String.IsNullOrWhiteSpace(downloadClient.Error))
                {
                    // Retry the download if an error is encountered.
                    LogError($"Download reported an error: {downloadClient.Error}", download, download.Torrent);
                    Log($"Download retry count {download.RetryCount}/{download.Torrent!.DownloadRetryAttempts}, torrent retry count {download.Torrent.RetryCount}/{download.Torrent.TorrentRetryAttempts}", download, download.Torrent);
                        
                    if (download.RetryCount < download.Torrent.DownloadRetryAttempts)
                    {
                        Log($"Retrying download", download, download.Torrent);

                        await _downloads.Reset(downloadId);
                        await _downloads.UpdateRetryCount(downloadId, download.RetryCount + 1);
                    }
                    else
                    {
                        Log($"Not retrying download", download, download.Torrent);

                        await _downloads.UpdateError(downloadId, downloadClient.Error);
                        await _downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);
                    }
                }
                else
                {
                    Log($"Download finished successfully", download, download.Torrent);

                    await _downloads.UpdateDownloadFinished(downloadId, DateTimeOffset.UtcNow);
                    await _downloads.UpdateUnpackingQueued(downloadId, DateTimeOffset.UtcNow);
                }

                ActiveDownloadClients.TryRemove(downloadId, out _);

                Log($"Removed from ActiveDownloadClients", download, download.Torrent);
            }
        }

        // Check if any torrents are finished unpacking, remove them from the active unpack list.
        var completedUnpacks = ActiveUnpackClients.Where(m => m.Value.Finished).ToList();

        if (completedUnpacks.Count > 0)
        {
            Log($"Processing {completedUnpacks.Count} completed unpacks");

            foreach (var (downloadId, unpackClient) in completedUnpacks)
            {
                var download = await _downloads.GetById(downloadId);

                if (download == null)
                {
                    ActiveUnpackClients.TryRemove(downloadId, out _);

                    Log($"Download with ID {downloadId} not found! Removed from unpack queue");

                    continue;
                }

                if (unpackClient.Error != null)
                {
                    Log($"Unpack reported an error: {unpackClient.Error}", download, download.Torrent);
                        
                    await _downloads.UpdateError(downloadId, unpackClient.Error);
                    await _downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);
                }
                else
                {
                    Log($"Unpack finished successfully", download, download.Torrent);

                    await _downloads.UpdateUnpackingFinished(downloadId, DateTimeOffset.UtcNow);
                    await _downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);
                }

                ActiveUnpackClients.TryRemove(downloadId, out _);

                Log($"Removed from ActiveUnpackClients", download, download.Torrent);
            }
        }

        var torrents = await _torrents.Get();

        // Process torrent retries
        foreach (var torrent in torrents.Where(m => m.Retry != null))
        {
            try
            {
                Log($"Retrying torrent {torrent.RetryCount}/{torrent.TorrentRetryAttempts}", torrent);

                if (torrent.RetryCount > torrent.TorrentRetryAttempts)
                {
                    await _torrents.UpdateRetry(torrent.TorrentId, null, torrent.RetryCount);
                    Log($"Torrent reach max retry count");
                    continue;
                }

                await _torrents.RetryTorrent(torrent.TorrentId, torrent.RetryCount);
            }
            catch (Exception ex)
            {
                await _torrents.UpdateRetry(torrent.TorrentId, null, torrent.RetryCount);
                await _torrents.UpdateError(torrent.TorrentId, ex.Message);
            }
        }

        // Process torrent errors
        foreach (var torrent in torrents.Where(m => m.Error != null && m.DeleteOnError > 0))
        {
            if (torrent.Completed == null)
            {
                continue;
            }

            if (torrent.Completed.Value.AddMinutes(torrent.DeleteOnError) > DateTime.UtcNow)
            {
                continue;
            }

            Log($"Removing torrent because it has been {torrent.DeleteOnError} minutes in the error state", torrent);

            await _torrents.Delete(torrent.TorrentId, true, true, true);
        }
            
        // Process torrent lifetime
        foreach (var torrent in torrents.Where(m => m.Downloads.Count == 0 && m.Completed == null && m.Lifetime > 0))
        {
            if (torrent.Added.AddMinutes(torrent.Lifetime) > DateTime.UtcNow)
            {
                continue;
            }

            Log($"Torrent has reached its {torrent.Lifetime} minutes lifetime, marking as error", torrent);

            await _torrents.UpdateRetry(torrent.TorrentId, null, torrent.TorrentRetryAttempts);
            await _torrents.UpdateComplete(torrent.TorrentId, $"Torrent lifetime of {torrent.Lifetime} minutes reached", DateTimeOffset.UtcNow, false);
        }

        torrents = await _torrents.Get();

        torrents = torrents.Where(m => m.Completed == null).ToList();

        if (torrents.Count > 0)
        {
            Log($"Processing {torrents.Count} torrents");
        }

        foreach (var torrent in torrents)
        {
            try
            {
                // Check if there are any downloads that are queued and can be started.
                var queuedDownloads = torrent.Downloads
                                             .Where(m => m.Completed == null && m.DownloadQueued != null && m.DownloadStarted == null && m.Error == null)
                                             .OrderBy(m => m.DownloadQueued)
                                             .ToList();

                _aggregatedDownloadResults.Clear();
                _aggregatedDownloadErrors.Clear();

                var downloadTasks = new List<Task>();

                foreach (var download in queuedDownloads)
                {
                    if (ActiveDownloadClients.Count >= settingDownloadLimit)
                    {
                        Log($"Not starting download because there are already the max number of downloads active", download, torrent);

                        break;
                    }

                    if (ActiveDownloadClients.ContainsKey(download.DownloadId))
                    {
                        Log($"Not starting download because this download is already active", download, torrent);

                        break;
                    }

                    try
                    {
                        if (download.Link == null)
                        {
                            Log($"Unrestricting links", download, torrent);

                            var downloadLink = await _torrents.UnrestrictLink(download.DownloadId);
                            download.Link = downloadLink;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Cannot unrestrict link: {ex.Message}", ex.Message);

                        await _downloads.UpdateError(download.DownloadId, ex.Message);
                        await _downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);
                        download.Error = ex.Message;
                        download.Completed = DateTimeOffset.UtcNow;

                        break;
                    }

                    Log($"Marking download as started", download, torrent);

                    download.DownloadStarted = DateTime.UtcNow;
                    await _downloads.UpdateDownloadStarted(download.DownloadId, download.DownloadStarted);

                    var downloadPath = settingDownloadPath;

                    if (!String.IsNullOrWhiteSpace(torrent.Category))
                    {
                        downloadPath = Path.Combine(downloadPath, torrent.Category);
                    }

                    Log($"Setting download path to {downloadPath}", download, torrent);

                    var downloadClient = new DownloadClient(download, torrent, downloadPath);

                    if (downloadClient.Type == Data.Enums.DownloadClient.Symlink)
                    {
                    }

                    downloadTasks.Add(StartDownload(download, torrent, downloadClient));

                    // Small delay not to spam the hell out of debrid service api...
                    await Task.Delay(100);

                }

                await Task.WhenAll(downloadTasks);

                if (_aggregatedDownloadResults.Count > 0)
                {
                    await _downloads.UpdateRemoteIdRange(_aggregatedDownloadResults);
                }
                if(_aggregatedDownloadErrors.Count > 0)
                {
                    await _downloads.UpdateErrorInRange(_aggregatedDownloadErrors);
                }

                // Check if there are any unpacks that are queued and can be started.
                var queuedUnpacks = torrent.Downloads
                                           .Where(m => m.Completed == null && m.UnpackingQueued != null && m.UnpackingStarted == null && m.Error == null)
                                           .OrderBy(m => m.DownloadQueued)
                                           .ToList();

                foreach (var download in queuedUnpacks)
                {
                    Log($"Starting unpack", download, torrent);

                    if (download.Link == null)
                    {
                        Log($"No download link found", download, torrent);

                        await _downloads.UpdateError(download.DownloadId, "Download Link cannot be null");
                        await _downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);

                        continue;
                    }

                    // Check if the unpacking process is even needed
                    var uri = new Uri(download.Link);
                    var fileName = uri.Segments.Last();

                    fileName = HttpUtility.UrlDecode(fileName);

                    Log($"Found file name {fileName}", download, torrent);

                    var extension = Path.GetExtension(fileName);

                    if (extension != ".rar" && extension != ".zip")
                    {
                        Log($"No need to unpack, setting it as unpacked", download, torrent);

                        download.UnpackingStarted = DateTimeOffset.UtcNow;
                        download.UnpackingFinished = DateTimeOffset.UtcNow;
                        download.Completed = DateTimeOffset.UtcNow;

                        await _downloads.UpdateUnpackingStarted(download.DownloadId, download.UnpackingStarted);
                        await _downloads.UpdateUnpackingFinished(download.DownloadId, download.UnpackingFinished);
                        await _downloads.UpdateCompleted(download.DownloadId, download.Completed);

                        continue;
                    }

                    if (Settings.Get.DownloadClient.Client == Data.Enums.DownloadClient.Symlink)
                    {
                        Log("Lets not unzip with symlink downloader...");

                        await _downloads.UpdateError(download.DownloadId, "Will not unzip with SymlinkDownloader!");
                        await _downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);

                        continue;
                    }

                        // Check if we have reached the download limit, if so queue the download, but don't start it.
                        if (TorrentRunner.ActiveUnpackClients.Count >= settingUnpackLimit)
                    {
                        Log($"Not starting unpack because there are already the max number of unpacks active", download, torrent);

                        continue;
                    }

                    if (TorrentRunner.ActiveUnpackClients.ContainsKey(download.DownloadId))
                    {
                        Log($"Not starting unpack because this download is already active", download, torrent);

                        continue;
                    }

                    download.UnpackingStarted = DateTimeOffset.UtcNow;
                    await _downloads.UpdateUnpackingStarted(download.DownloadId, download.UnpackingStarted);

                    var downloadPath = settingDownloadPath;

                    if (!String.IsNullOrWhiteSpace(torrent.Category))
                    {
                        downloadPath = Path.Combine(downloadPath, torrent.Category);
                    }

                    Log($"Setting unpack path to {downloadPath}", download, torrent);

                    // Start the unpacking process
                    var unpackClient = new UnpackClient(download, downloadPath);

                    if (TorrentRunner.ActiveUnpackClients.TryAdd(download.DownloadId, unpackClient))
                    {
                        Log($"Starting unpack", download, torrent);

                        unpackClient.Start();
                    }
                }

                Log("Processing", torrent);

                // If torrent is erroring out on the Real-Debrid side.
                if (torrent.RdStatus == TorrentStatus.Error)
                {
                    Log($"Torrent reported an error: {torrent.RdStatusRaw}", torrent);
                    Log($"Torrent retry count {torrent.RetryCount}/{torrent.TorrentRetryAttempts}", torrent);

                    Log($"Received RealDebrid error: {torrent.RdStatusRaw}, not processing further", torrent);

                    await _torrents.UpdateComplete(torrent.TorrentId, $"Received RealDebrid error: {torrent.RdStatusRaw}.", DateTimeOffset.UtcNow, true);

                    continue;
                }

                // Real-Debrid is waiting for file selection, select which files to download.
                if ((torrent.RdStatus == TorrentStatus.WaitingForFileSelection || torrent.RdStatus == TorrentStatus.Finished) &&
                    torrent.FilesSelected == null &&
                    torrent.Downloads.Count == 0)
                {
                    Log($"Selecting files", torrent);

                    await _torrents.SelectFiles(torrent.TorrentId);

                    await _torrents.UpdateFilesSelected(torrent.TorrentId, DateTime.UtcNow);
                }

                // Real-Debrid finished downloading the torrent, process the file to host.
                if (torrent.RdStatus == TorrentStatus.Finished)
                {
                    // The files are selected but there are no downloads yet, check if Real-Debrid has generated links yet.
                    if (torrent.Downloads.Count == 0 && torrent.FilesSelected != null)
                    {
                        Log($"Creating downloads", torrent);

                        if (torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadAll)
                        {
                            await _torrents.CreateDownloads(torrent.TorrentId);
                        }
                    }
                }

                // Check if torrent is complete, or if we don't want to download any files to the host.
                if ((torrent.Downloads.Count > 0) || 
                    torrent.RdStatus == TorrentStatus.Finished && torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadNone)
                {

                    var completeCount = torrent.Downloads.Count(m => m.Completed != null);

                    var completePerc = 0;

                    var totalDownloadBytes = torrent.Downloads.Sum(m => m.BytesTotal);
                    var totalDoneBytes = torrent.Downloads.Sum(m => m.BytesDone);

                    if (totalDownloadBytes > 0)
                    {
                        completePerc = (Int32)((Double)totalDoneBytes / totalDownloadBytes * 100);
                    }

                    if (completeCount == torrent.Downloads.Count)
                    {

                        await _torrents.UpdateComplete(torrent.TorrentId, null, DateTimeOffset.UtcNow, true);

                        Log($"All downloads complete, marking torrent as complete", torrent);

                        if (torrent.Category.ToLower() == "sonarr")
                        {
                            string seriesName = ExtractSeriesNameFromRdName(torrent.RdName, torrent.Category);
                            Log($"Nom de la série (Sonarr) : {seriesName}");
                            int? seriesId = await GetSerieIdFromNameAsync(seriesName, torrent.Category);
                            int? theTvdbId = null;
                            theTvdbId = await GetSerieIdFromNameAsync(seriesName, torrent.Category);
                            Log($"Numero ID TVDB : {theTvdbId }");
                            await AddSeriesToSonarr(theTvdbId, seriesName, torrent.Category, Settings.Get.General.RadarrSonarrInstanceConfigPath);
                        }
                        else if (torrent.Category.ToLower() == "radarr")
                        {
                            string seriesName = ExtractSeriesNameFromRdName(torrent.RdName, torrent.Category);
                            Log($"Nom du Film (Radarr) : {seriesName}");
                            int? seriesId = await GetMovieIdFromNameAsync(seriesName, torrent.Category, Settings.Get.General.RadarrSonarrInstanceConfigPath);
                            int? theTvdbId = null;
                            theTvdbId = await GetMovieIdFromNameAsync(seriesName, torrent.Category, Settings.Get.General.RadarrSonarrInstanceConfigPath);
                            Log($"Numero ID TMDB : {theTvdbId }");
                            await AddMovieToRadarr(theTvdbId, seriesName, torrent.Category, Settings.Get.General.RadarrSonarrInstanceConfigPath);
                        }
                        else
                        {
                        Log($"Catégorie de torrent inconnue : {torrent.Category}");
                        }

                        if (!String.IsNullOrWhiteSpace(Settings.Get.General.RadarrSonarrInstanceConfigPath))
                        {
                            await TryRefreshMonitoredDownloadsAsync(torrent.Category, Settings.Get.General.RadarrSonarrInstanceConfigPath);
                        }

                        if (!String.IsNullOrWhiteSpace(Settings.Get.General.CopyAddedTorrents))
                        {
                            var sourceFilePath = Path.Combine(Settings.Get.DownloadClient.MappedPath, "tempTorrentsFiles", $"{torrent.RdName}.torrent");
                            var targetFilePath = Path.Combine(Settings.Get.General.CopyAddedTorrents, $"{torrent.RdName}.torrent");

                            _logger.LogInformation($"Attempting to move file {torrent.RdName}.torrent");

                            if (File.Exists(sourceFilePath))
                            {
                                if (File.Exists(targetFilePath))
                                {
                                    File.Delete(targetFilePath);
                                }
                                File.Move(sourceFilePath, targetFilePath);
                                _logger.LogInformation($"Moved {torrent.RdName}.torrent from tempTorrentsFiles to the final directory.");
                            }
                        }

                        switch (torrent.FinishedAction)
                        {
                            case TorrentFinishedAction.RemoveAllTorrents:
                                Log($"Removing torrents from Real-Debrid and Real-Debrid Client, no files", torrent);
                                await _torrents.Delete(torrent.TorrentId, true, true, false);

                                break;
                            case TorrentFinishedAction.RemoveRealDebrid:
                                Log($"Removing torrents from Real-Debrid, no files", torrent);
                                await _torrents.Delete(torrent.TorrentId, false, true, false);

                                break;
                            case TorrentFinishedAction.RemoveClient:
                                Log($"Removing torrents from client, no files", torrent);
                                await _torrents.Delete(torrent.TorrentId, true, false, false);

                                break;
                            case TorrentFinishedAction.None:
                                Log($"Not removing torrents or files", torrent);

                                break;
                            default:
                                Log($"Invalid torrent FinishedAction {torrent.FinishedAction}", torrent);

                                break;
                        }
                        try
                        {
                            await _torrents.RunTorrentComplete(torrent.TorrentId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex.Message, "Unable to run post process: {Message}", ex.Message);
                        }
                    }
                    else
                    {
                        Log($"Waiting for downloads to complete. {completeCount}/{torrent.Downloads.Count} complete ({completePerc}%)", torrent);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, "Torrent processing result in an unexpected exception: {Message}", ex.Message);
                await _torrents.UpdateComplete(torrent.TorrentId, ex.Message, DateTimeOffset.UtcNow, true);
            }
        }
            
        await _remoteService.Update();

        sw.Stop();

        if (sw.ElapsedMilliseconds > 1000)
        {
            Log($"TorrentRunner Tick End (took {sw.ElapsedMilliseconds}ms)");
        }
    }

private async Task<int?> GetSerieIdFromNameAsync(string seriesName, string category)
{
    try
    {
        string searchUrl = $"https://api.tvmaze.com/search/shows?q={HttpUtility.UrlEncode(seriesName)}";

        using (HttpClient httpClient = new HttpClient())
        {
            HttpResponseMessage response = await httpClient.GetAsync(searchUrl);

            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();

                JsonDocument jsonDoc = JsonDocument.Parse(jsonResponse);
                JsonElement root = jsonDoc.RootElement;

                if (root.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement firstElement = root[0];

                if (firstElement.TryGetProperty("show", out JsonElement showElement))
                {
                    if (showElement.TryGetProperty("externals", out JsonElement externalsElement))
                    {
                        if (externalsElement.TryGetProperty("thetvdb", out JsonElement tvdbElement))
                        {
                            if (tvdbElement.ValueKind == JsonValueKind.Number)
                            {
                                return tvdbElement.GetInt32();
                            }
                        }
                    }
                }

                return null;
                }
            else
            {
                _logger.LogError($"La requête API TVMaze a échoué : {response.ReasonPhrase}");
                return null;
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogError($"Une erreur est survenue lors de la recherche de l'ID de la Série : {ex.Message}");
        return null;
    }
}

private async Task<int?> GetMovieIdFromNameAsync(string seriesName, string categoryInstance, string configFilePath)
{
    try
    {
        var apiConfig = await GetApiConfigAsync(categoryInstance, configFilePath); // load comme ça
        if (apiConfig == null)
        {
            return null;
        }

        string searchUrl = $"https://api.themoviedb.org/3/search/movie?api_key={apiConfig.Value.TmdbApi}&query={HttpUtility.UrlEncode(seriesName)}";

        using (HttpClient httpClient = new HttpClient())
        {
            HttpResponseMessage response = await httpClient.GetAsync(searchUrl);

            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();

                dynamic result = JObject.Parse(jsonResponse);
                int? seriesId = result.results[0]?.id;

                return seriesId; // Retourne l'ID de la série (peut être null si non trouvé)
            }
            else
            {
                _logger.LogError($"La requête API TMDb a échoué : {response.ReasonPhrase}");
                return null; // Retourne null en cas d'échec de la requête
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogError($"Une erreur est survenue lors de la recherche de l'ID du Film : {ex.Message}");
        return null; // Retourne null en cas d'erreur
    }
}

public class TvMazeSearchResult
{
    public TvMazeShow Show { get; set; }
}

public class TvMazeShow
{
    public TvMazeExternals Externals { get; set; }
}

public class TvMazeExternals
{
    public string TheTvdb { get; set; }
}

public string ExtractSeriesNameFromRdName(string rdName, string category)
{
    if (string.IsNullOrWhiteSpace(rdName))
    {
        _logger.LogError("Le nom du fichier est vide ou null.");
        return null;
    }

    _logger.LogInformation($"Nom du fichier : {rdName}");

    rdName = rdName.Replace(".", " ");
    _logger.LogInformation($"Nom du fichier après remplacement des points : {rdName}");

    rdName = Regex.Replace(rdName, @"^\w\s+", " ");
    _logger.LogInformation($"Nom du fichier après remplacement des caractères spéciaux : {rdName}");

    rdName = Regex.Replace(rdName, @"\[.*?\]", "");
    _logger.LogInformation($"Nom du fichier après exclusion du contenu entre crochets : {rdName}");

    if (rdName.EndsWith(" mkv", StringComparison.OrdinalIgnoreCase))
    {
        rdName = rdName.Substring(0, rdName.Length - 4); 
        _logger.LogInformation($"Nom du fichier après exclusion de l'extension mkv : {rdName}");
    }

    // A cet endroit je peux mettre des exclusions de mots
    string seriesPattern = @"^(.+?)(?=\d{4}\sS\d{2}|Saison|Complete|Integrale|\d|S\d)";

    Match match = Regex.Match(rdName, seriesPattern);

    if (!match.Success)
    {
        _logger.LogError("Impossible de trouver le titre de la série.");
        return null;
    }

    string seriesName = match.Groups[1].Value.Trim();
    return seriesName;
}

private async Task<bool> AddSeriesToSonarr(int? theTvdbId, string seriesName, string categoryInstance, string configFilePath)
{
    try
    {
        var apiConfig = await GetApiConfigAsync(categoryInstance, configFilePath); // Charger la configuration API

        if (apiConfig == null)
        {
            _logger.LogError("La configuration API n'a pas pu être récupérée.");
            return false;
        }

        // Débogage : afficher les valeurs de ApiKey et Host
        _logger.LogDebug($"ApiKey : {apiConfig.Value.ApiKey}");
        _logger.LogDebug($"Host : {apiConfig.Value.Host}");
        _logger.LogDebug($"RootFolderPath : {apiConfig.Value.RootFolderPath}");
        _logger.LogDebug($"qualityProfileId : {apiConfig.Value.qualityProfileId}");

        if (!theTvdbId.HasValue || string.IsNullOrWhiteSpace(seriesName))
        {
            _logger.LogError("Impossible d'ajouter la Série : ID TheTVDB ou nom de la Série manquante.");
            return false;
        }

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiConfig.Value.ApiKey);

        var requestData = new
        {
            TvdbId = theTvdbId.Value,
            title = seriesName,
            qualityProfileId = apiConfig.Value.qualityProfileId,
            RootFolderPath = apiConfig.Value.RootFolderPath,
            monitored = true
        };

        var json = JsonSerializer.Serialize(requestData);
        var data = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{apiConfig.Value.Host}/api/v3/series", data);


        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Série ajouté avec succès à Sonarr.");
            return true;
        }
        else
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogDebug($"Contenu de la réponse : {responseContent}");
                if(responseContent.Contains("This series has already been added"))
                {
                    _logger.LogDebug("La série existe déjà dans Sonarr.");
                }
                else
                {
                    _logger.LogError($"Échec de l'ajout de la série à Sonarr : {response.ReasonPhrase}. Contenu de la réponse : {responseContent}");
                }

            return false;
        }
    }
    catch (Exception ex)
    {
        _logger.LogError($"Erreur lors de l'ajout du film à Sonarr : {ex.Message}");
        return false;
    }
}

private async Task<bool> AddMovieToRadarr(int? theTvdbId, string seriesName, string categoryInstance, string configFilePath)
{
    try
    {
        var apiConfig = await GetApiConfigAsync(categoryInstance, configFilePath); // Charger la configuration API

        if (apiConfig == null)
        {
            _logger.LogError("La configuration API n'a pas pu être récupérée.");
            return false;
        }

        // Débogage : afficher les valeurs de ApiKey et Host
        _logger.LogDebug($"ApiKey : {apiConfig.Value.ApiKey}");
        _logger.LogDebug($"Host : {apiConfig.Value.Host}");
        _logger.LogDebug($"RootFolderPath : {apiConfig.Value.RootFolderPath}");
        _logger.LogDebug($"qualityProfileId : {apiConfig.Value.qualityProfileId}");

        if (!theTvdbId.HasValue || string.IsNullOrWhiteSpace(seriesName))
        {
            _logger.LogError("Impossible d'ajouter le film à Radarr : ID TheTVDB ou nom du film manquant.");
            return false;
        }

        var requestData = new
        {
            tmdbId = theTvdbId.Value,
            title = seriesName,
            qualityProfileId = apiConfig.Value.qualityProfileId,
            RootFolderPath = apiConfig.Value.RootFolderPath,
            monitored = true
        };

        var json = JsonSerializer.Serialize(requestData);
        var data = new StringContent(json, Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiConfig.Value.ApiKey);
        
        var response = await _httpClient.PostAsync($"{apiConfig.Value.Host}/api/v3/movie", data);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Film ajouté avec succès à Radarr.");
            return true;
        }
        else
        {
            var responseContent = await response.Content.ReadAsStringAsync();
                if(responseContent.Contains("This movie has already been added"))
                {
                    _logger.LogDebug("Le Film existe déjà dans Radarr.");
                }
                else
                {
                    _logger.LogError($"Échec de l'ajout de la série à Radarr : {response.ReasonPhrase}. Contenu de la réponse : {responseContent}");
                }

            return false;
        }
    }
    catch (Exception ex)
    {
        _logger.LogError($"Erreur lors de l'ajout du film à Radarr : {ex.Message}");
        return false;
    }
}

private async Task<bool> TryRefreshMonitoredDownloadsAsync(string categoryInstance, string configFilePath)
{
    try
    {
        var apiConfig = await GetApiConfigAsync(categoryInstance, configFilePath); // load comme ça
        if (apiConfig == null)
        {
            return false;
        }

        var data = new StringContent("{\"name\":\"RefreshMonitoredDownloads\"}", Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiConfig.Value.ApiKey); // utilisé comme ça ici
        var response = await _httpClient.PostAsync($"{apiConfig.Value.Host}/api/v3/command", data); // et ici pour host

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogInformation($"Réponse de l'API : {responseBody}");
            return true;
        }
        else
        {
            _logger.LogError("La requête API a échoué.");
            return false;
        }
    }
    catch (Exception ex)
    {
        _logger.LogError($"Une erreur est survenue : {ex.Message}");
        return false;
    }
}

public struct ApiConfig 
{
    public string Host { get; set; }
    public string ApiKey { get; set; }
    public string RootFolderPath { get; set; }
    public string qualityProfileId { get; set; }
    public string TmdbApi { get; set; }
}

private async Task<ApiConfig?> GetApiConfigAsync(string categoryInstance, string configFilePath)
{
    try
    {
        string jsonString = await File.ReadAllTextAsync(configFilePath);
        JsonDocument doc = JsonDocument.Parse(jsonString);

        if (!doc.RootElement.TryGetProperty(categoryInstance, out JsonElement category))
        {
            _logger.LogError($"La catégorie {categoryInstance} n'est pas trouvée dans le fichier de configuration.");
            return null;
        }

        var host = category.GetProperty("Host").GetString();
        var apiKey = category.GetProperty("ApiKey").GetString();
        var folder = category.GetProperty("RootFolderPath").GetString();
        var quality = category.GetProperty("qualityProfileId").GetString();
        var tmdb = categoryInstance == "radarr" ? category.GetProperty("TmdbApi").GetString() : null;

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(quality) ||
            (categoryInstance == "radarr" && string.IsNullOrEmpty(tmdb)))
        {
            return null;
        }

        return new ApiConfig { Host = host, ApiKey = apiKey, RootFolderPath = folder, qualityProfileId = quality, TmdbApi = tmdb };
    }
    catch (Exception ex)
    {
        _logger.LogError($"Une erreur est survenue lors de la lecture du fichier de configuration : {ex.Message}");
        return null;
    }
}

    private void Log(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        _logger.LogDebug(message);
    }

    private void Log(String message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        _logger.LogDebug(message);
    }

    private void LogError(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        _logger.LogError(message);
    }

    private async Task StartDownload(Download download, Torrent torrent, DownloadClient downloadClient)
    {
        if (ActiveDownloadClients.TryAdd(download.DownloadId, downloadClient))
        {
            Log($"Starting download", download, torrent);

            var remoteId = await downloadClient.Start();

            if (!string.IsNullOrWhiteSpace(remoteId) && download.RemoteId != remoteId)
            {
                Log($"Received ID {remoteId}", download, torrent);
                _aggregatedDownloadResults.Add(download.DownloadId, remoteId);
            }
            else
            {
                Log($"Did not recieve ID {remoteId}", download, torrent);
                _aggregatedDownloadErrors.Add(download.DownloadId, download.Error);
            }
        }
    }
}
