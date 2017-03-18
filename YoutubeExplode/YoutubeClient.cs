﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YoutubeExplode.Internal;
using YoutubeExplode.Models;
using YoutubeExplode.Services;

namespace YoutubeExplode
{
    /// <summary>
    /// YoutubeClient
    /// </summary>
    public partial class YoutubeClient : IDisposable
    {
        private readonly IRequestService _requestService;
        private readonly Dictionary<string, PlayerSource> _playerSourceCache = new Dictionary<string, PlayerSource>();

        /// <summary>
        /// Creates an instance of <see cref="YoutubeClient"/> with custom services
        /// </summary>
        public YoutubeClient(IRequestService requestService)
        {
            if (requestService == null)
                throw new ArgumentNullException(nameof(requestService));

            _requestService = requestService;
        }

        /// <summary>
        /// Creates an instance of <see cref="YoutubeClient"/> with default services
        /// </summary>
        public YoutubeClient()
            : this(new DefaultRequestService())
        {
        }

        /// <inheritdoc />
        ~YoutubeClient()
        {
            Dispose(false);
        }

        private async Task<PlayerSource> GetPlayerSourceAsync(string version)
        {
            if (version == null)
                throw new ArgumentNullException(nameof(version));

            // Try get cached player source
            var playerSource = _playerSourceCache.GetOrDefault(version);

            // If not available - decompile a new one
            if (playerSource == null)
            {
                // Get
                string url = $"https://www.youtube.com/yts/jsbin/player-{version}/base.js";
                string response = await _requestService.GetStringAsync(url).ConfigureAwait(false);

                // Parse
                playerSource = Parser.PlayerSourceFromJs(response);
                playerSource.Version = version;

                // Cache
                _playerSourceCache[version] = playerSource;
            }

            return playerSource;
        }

        private async Task DecipherAsync(VideoInfo videoInfo, string playerVersion)
        {
            if (videoInfo == null)
                throw new ArgumentNullException(nameof(videoInfo));
            if (!videoInfo.NeedsDeciphering)
                throw new Exception("Given video info does not need to be deciphered");

            // Get player source
            var playerSource = await GetPlayerSourceAsync(playerVersion).ConfigureAwait(false);

            // Decipher streams
            foreach (var streamInfo in videoInfo.Streams.Where(s => s.NeedsDeciphering))
            {
                string sig = streamInfo.Signature;
                string newSig = playerSource.Decipher(sig);
                streamInfo.Url = streamInfo.Url.SetQueryParameter("signature", newSig);
                streamInfo.NeedsDeciphering = false;
            }

            // Decipher dash manifest
            if (videoInfo.DashManifest != null && videoInfo.DashManifest.NeedsDeciphering)
            {
                string sig = videoInfo.DashManifest.Signature;
                string newSig = playerSource.Decipher(sig);
                videoInfo.DashManifest.Url = videoInfo.DashManifest.Url.SetPathParameter("signature", newSig);
                videoInfo.DashManifest.NeedsDeciphering = false;
            }
        }

        private async Task<long> GetContentLengthAsync(string url)
        {
            if (url == null)
                throw new ArgumentNullException(nameof(url));

            // Get the headers
            var headers = await _requestService.GetHeadersAsync(url).ConfigureAwait(false);

            // Get file size header
            if (!headers.ContainsKey("Content-Length"))
                throw new Exception("Content-Length header not found");

            return headers["Content-Length"].ParseLong();
        }

        /// <summary>
        /// Checks whether a video with the given ID exists
        /// </summary>
        public async Task<bool> CheckVideoExistsAsync(string videoId)
        {
            if (videoId == null)
                throw new ArgumentNullException(nameof(videoId));
            if (!ValidateVideoId(videoId))
                throw new ArgumentException("Is not a valid Youtube video ID", nameof(videoId));

            // Get
            string url = $"https://www.youtube.com/get_video_info?video_id={videoId}&el=info&ps=default";
            string response = await _requestService.GetStringAsync(url).ConfigureAwait(false);

            // Parse
            var dic = Parser.DictionaryFromUrlEncoded(response);
            string status = dic.GetOrDefault("status");
            int errorCode = dic.GetOrDefault("errorcode").ParseIntOrDefault();
            return !(status.EqualsInvariant("fail") && errorCode.IsEither(100, 150));
        }

        /// <summary>
        /// Gets video metadata by id
        /// </summary>
        public async Task<VideoInfo> GetVideoInfoAsync(string videoId)
        {
            if (videoId == null)
                throw new ArgumentNullException(nameof(videoId));
            if (!ValidateVideoId(videoId))
                throw new ArgumentException("Is not a valid Youtube video ID", nameof(videoId));

            // Get video context
            string url = $"https://www.youtube.com/embed/{videoId}";
            string response = await _requestService.GetStringAsync(url).ConfigureAwait(false);

            // Parse video context
            var videoContext = Parser.VideoContextFromHtml(response);

            // Get video info
            url = $"https://www.youtube.com/get_video_info?video_id={videoId}&sts={videoContext.Sts}&el=info&ps=default";
            response = await _requestService.GetStringAsync(url).ConfigureAwait(false);

            // Parse video info
            var result = Parser.VideoInfoFromUrlEncoded(response);

            // Get extended video info
            url = $"https://www.youtube.com/get_video_metadata?video_id={videoId}";
            response = await _requestService.GetStringAsync(url).ConfigureAwait(false);

            // Parse extended video info and copy metadata
            var extendedVideoInfo = Parser.ExtendedVideoInfoFromXml(response);
            result.Author = extendedVideoInfo.Author;
            result.Description = extendedVideoInfo.Description;
            result.LikeCount = extendedVideoInfo.LikeCount;
            result.DislikeCount = extendedVideoInfo.DisikeCount;

            // Decipher
            if (result.NeedsDeciphering)
                await DecipherAsync(result, videoContext.PlayerVersion).ConfigureAwait(false);

            // Get additional streams from dash if available
            if (result.DashManifest != null)
            {
                // Get
                response = await _requestService.GetStringAsync(result.DashManifest.Url).ConfigureAwait(false);

                // Parse and concat with new streams
                var dashStreams = Parser.MediaStreamInfosFromXml(response);
                result.Streams = result.Streams.Concat(dashStreams).ToArray();
            }

            // Get file size of streams that don't have it yet
            foreach (var streamInfo in result.Streams.Where(s => s.FileSize == 0))
                streamInfo.FileSize = await GetContentLengthAsync(streamInfo.Url).ConfigureAwait(false);

            // Finalize the stream list
            result.Streams = result.Streams
                .Distinct(s => s.Itag) // only one stream per itag
                .OrderByDescending(s => s.Quality) // sort by quality
                .ThenByDescending(s => s.Bitrate) // then by bitrate
                .ThenByDescending(s => s.FileSize) // then by filesize
                .ThenByDescending(s => s.ContainerType) // then by type
                .ToArray();

            return result;
        }

        /// <summary>
        /// Gets playlist metadata by id
        /// </summary>
        public async Task<PlaylistInfo> GetPlaylistInfoAsync(string playlistId)
        {
            // Original code credit: https://github.com/dr-BEat

            if (playlistId == null)
                throw new ArgumentNullException(nameof(playlistId));
            if (!ValidatePlaylistId(playlistId))
                throw new ArgumentException("Is not a valid Youtube playlist ID", nameof(playlistId));

            // Can also use `action_get_user_uploads_by_user=1` for user uploads...

            // Get
            string url = $"https://www.youtube.com/list_ajax?style=xml&action_get_list=1&list={playlistId}";
            string response = await _requestService.GetStringAsync(url).ConfigureAwait(false);

            // Parse
            var result = Parser.PlaylistInfoFromXml(response);
            result.Id = playlistId;

            // Keep requesting again with offset to make sure we get everything
            bool hasMore = result.VideoIds.Count > 0;
            int offset = result.VideoIds.Count;
            while (hasMore)
            {
                // Get
                string nextUrl = url + $"&index={offset}";
                response = await _requestService.GetStringAsync(nextUrl).ConfigureAwait(false);

                // Parse and concat ids
                var extension = Parser.PlaylistInfoFromXml(response);
                int delta = result.VideoIds.Count;
                result.VideoIds = result.VideoIds.Concat(extension.VideoIds).Distinct().ToArray();
                delta = result.VideoIds.Count - delta;

                // Go for the next batch
                hasMore = delta > 0;
                offset += extension.VideoIds.Count;
            }

            return result;
        }

        /// <summary>
        /// Gets media stream by its metadata
        /// </summary>
        public async Task<MediaStream> GetMediaStreamAsync(MediaStreamInfo streamInfo)
        {
            if (streamInfo == null)
                throw new ArgumentNullException(nameof(streamInfo));
            if (streamInfo.Url == null)
                throw new Exception("Given stream info does not have a URL");
            if (streamInfo.NeedsDeciphering)
                throw new Exception("Given stream's signature needs to be deciphered first");

            // Get
            var stream = await _requestService.GetStreamAsync(streamInfo.Url).ConfigureAwait(false);

            // Pack
            var result = new MediaStream(stream, streamInfo);

            return result;
        }

        /// <summary>
        /// Gets closed caption track by its metadata
        /// </summary>
        public async Task<ClosedCaptionTrack> GetClosedCaptionTrackAsync(ClosedCaptionTrackInfo closedCaptionTrackInfo)
        {
            if (closedCaptionTrackInfo == null)
                throw new ArgumentNullException(nameof(closedCaptionTrackInfo));
            if (closedCaptionTrackInfo.Url == null)
                throw new Exception("Given caption track info does not have a URL");

            // Get
            string response = await _requestService.GetStringAsync(closedCaptionTrackInfo.Url).ConfigureAwait(false);

            // Parse
            var result = Parser.ClosedCaptionTrackFromXml(response);
            result.Info = closedCaptionTrackInfo;

            return result;
        }

        /// <summary>
        /// Dispose method
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                (_requestService as IDisposable)?.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public partial class YoutubeClient
    {
        /// <summary>
        /// Verifies that the given string is syntactically a valid Youtube video ID
        /// </summary>
        public static bool ValidateVideoId(string videoId)
        {
            if (videoId.IsBlank())
                return false;

            if (videoId.Length != 11)
                return false;

            return !Regex.IsMatch(videoId, @"[^0-9a-zA-Z_\-]");
        }

        /// <summary>
        /// Tries to parse video ID from a youtube video URL
        /// </summary>
        public static bool TryParseVideoId(string videoUrl, out string videoId)
        {
            videoId = default(string);

            if (videoUrl.IsBlank())
                return false;

            // https://www.youtube.com/watch?v=yIVRs6YSbOM
            string regularMatch =
                Regex.Match(videoUrl, @"youtube\..+?/watch.*?v=(.*?)(?:&|/|$)").Groups[1].Value;
            if (regularMatch.IsNotBlank() && ValidateVideoId(regularMatch))
            {
                videoId = regularMatch;
                return true;
            }

            // https://youtu.be/yIVRs6YSbOM
            string shortMatch =
                Regex.Match(videoUrl, @"youtu.be/(.*?)(?:\?|&|/|$)").Groups[1].Value;
            if (shortMatch.IsNotBlank() && ValidateVideoId(shortMatch))
            {
                videoId = shortMatch;
                return true;
            }

            // https://www.youtube.com/embed/yIVRs6YSbOM
            string embedMatch =
                Regex.Match(videoUrl, @"youtube\..+?/embed/(.*?)(?:\?|&|/|$)").Groups[1].Value;
            if (embedMatch.IsNotBlank() && ValidateVideoId(embedMatch))
            {
                videoId = embedMatch;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Parses video ID from a Youtube video URL
        /// </summary>
        public static string ParseVideoId(string videoUrl)
        {
            if (videoUrl == null)
                throw new ArgumentNullException(nameof(videoUrl));

            string result;
            bool success = TryParseVideoId(videoUrl, out result);
            if (success)
                return result;

            throw new FormatException($"Could not parse video ID from given string [{videoUrl}]");
        }

        /// <summary>
        /// Verifies that the given string is syntactically a valid Youtube playlist ID
        /// </summary>
        public static bool ValidatePlaylistId(string playlistId)
        {
            if (playlistId.IsBlank())
                return false;

            if (!playlistId.Length.IsEither(2, 13, 18, 24, 34))
                return false;

            return !Regex.IsMatch(playlistId, @"[^0-9a-zA-Z_\-]");
        }

        /// <summary>
        /// Tries to parse playlist ID from a Youtube playlist URL
        /// </summary>
        public static bool TryParsePlaylistId(string playlistUrl, out string playlistId)
        {
            playlistId = default(string);

            if (playlistUrl.IsBlank())
                return false;

            // https://www.youtube.com/playlist?list=PLOU2XLYxmsIJGErt5rrCqaSGTMyyqNt2H
            string regularMatch =
                Regex.Match(playlistUrl, @"youtube\..+?/playlist.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
            if (regularMatch.IsNotBlank() && ValidatePlaylistId(regularMatch))
            {
                playlistId = regularMatch;
                return true;
            }

            // https://www.youtube.com/watch?v=b8m9zhNAgKs&list=PL9tY0BWXOZFuFEG_GtOBZ8-8wbkH-NVAr
            string compositeMatch =
                Regex.Match(playlistUrl, @"youtube\..+?/watch.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
            if (compositeMatch.IsNotBlank() && ValidatePlaylistId(compositeMatch))
            {
                playlistId = compositeMatch;
                return true;
            }

            // https://youtu.be/b8m9zhNAgKs/?list=PL9tY0BWXOZFuFEG_GtOBZ8-8wbkH-NVAr
            string shortCompositeMatch =
                Regex.Match(playlistUrl, @"youtu.be/.*?/.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
            if (shortCompositeMatch.IsNotBlank() && ValidatePlaylistId(shortCompositeMatch))
            {
                playlistId = shortCompositeMatch;
                return true;
            }

            // https://www.youtube.com/embed/b8m9zhNAgKs/?list=PL9tY0BWXOZFuFEG_GtOBZ8-8wbkH-NVAr
            string embedCompositeMatch =
                Regex.Match(playlistUrl, @"youtube\..+?/embed/.*?/.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
            if (embedCompositeMatch.IsNotBlank() && ValidatePlaylistId(embedCompositeMatch))
            {
                playlistId = embedCompositeMatch;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Parses playlist ID from a Youtube playlist URL
        /// </summary>
        public static string ParsePlaylistId(string playlistUrl)
        {
            if (playlistUrl == null)
                throw new ArgumentNullException(nameof(playlistUrl));

            string result;
            bool success = TryParsePlaylistId(playlistUrl, out result);
            if (success)
                return result;

            throw new FormatException($"Could not parse playlist ID from given string [{playlistUrl}]");
        }
    }
}