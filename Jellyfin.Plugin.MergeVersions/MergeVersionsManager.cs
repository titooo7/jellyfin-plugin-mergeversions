using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Dlna;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IServerEntryPoint
    {
        // Various dependencies needed for the plugin
        private readonly ILibraryManager _libraryManager;
        private readonly VideosController _videosController;
        private readonly Timer _timer;
        private readonly ILogger<VideosController> _logger; // TODO: Logging
        private readonly IUserManager _userManager;
        private readonly SessionInfo _session;
        private readonly IFileSystem _fileSystem;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            ILogger<VideosController> logger,
            IServerConfigurationManager serverConfigurationManager,
            IUserManager userManager,
            IDtoService dtoService,
            IAuthorizationContext authContext,
            IFileSystem fileSystem,
            IDlnaManager dlnaManager,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            ISubtitleEncoder subtitleEncoder,
            IDeviceManager deviceManager,
            TranscodingJobHelper transcodingJobHelper
        )
        {
            // Initialize and store the dependencies
            _libraryManager = libraryManager;
            _userManager = userManager;
            _logger = logger;
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
            _videosController = new VideosController(
                _libraryManager,
                _userManager,
                dtoService,
                dlnaManager,
                authContext,
                mediaSourceManager,
                serverConfigurationManager,
                mediaEncoder,
                deviceManager,
                transcodingJobHelper,
                null,
                null
            );
            _fileSystem = fileSystem;
        }

        // Function to get a list of movies from the library
        private IEnumerable<Movie> GetMoviesFromLibrary()
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true,
                HasTmdbId = true,
            }).Select(m => m as Movie);

            return movies.ToList();
        }

        // Function to get a list of TV episodes from the library
        private IEnumerable<Episode> GetEpisodesFromLibrary()
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true,
            }).Select(m => m as Episode);

            return episodes.ToList();
        }

        // Function to merge movies with multiple versions
        public void MergeMovies(IProgress<double> progress)
        {
            var movies = GetMoviesFromLibrary().ToArray();

            _logger.LogInformation("Scanning for repeated movies");

            // below line Group movies by Tmdb Id and select those with more than 1 in the group
            //var duplications = movies.GroupBy(x => new { V = x.ProviderIds["Tmdb"] }).Where(x => x.Count() > 1).ToList();
            // the three lines below replace the commented out above, it was something that I added instead of the line above the problem is that parentId is a folder and not a library
            var duplications = movies.GroupBy(x => new { TMDbId = x.ProviderIds["Tmdb"], TopParentId = x.ParentId })
            .Where(x => x.Count() > 1)
            .ToList();
            var total = duplications.Count();
            var current = 0;

            // For each group of duplications, merge the non-merged movies
            Parallel.ForEach(duplications, m =>
            {
                current++;
                var percent = ((double)current / (double)total) * 100;
                progress?.Report((int)percent);

                MergeVideos(m.Where(m => m.PrimaryVersionId == null && m.GetLinkedAlternateVersions().Count() == 0).ToList());
            });

            progress?.Report(100);
        }

        // Function to split movies
        public void SplitMovies(IProgress<double> progress)
        {
            var movies = GetMoviesFromLibrary().ToArray();
            movies = movies.Where(isElegible).ToArray();
            var total = movies.Count();
            var current = 0;

            // For each movie, split it
            Parallel.ForEach(movies, m =>
            {
                current++;
                var percent = ((double)current / (double)total) * 100;
                progress?.Report((int)percent);

                _logger.LogInformation($"Splitting {m.Name} ({m.ProductionYear})");
                SplitVideo(m);
            });

            progress?.Report(100);
        }

        // Function to split a video (delete alternate sources)
        private void SplitVideo(BaseItem v)
        {
            _videosController.DeleteAlternateSources(v.Id);
        }

        // Function to merge videos (movies)
        private void MergeVideos(IEnumerable<BaseItem> videos)
        {
            List<BaseItem> elegibleToMerge = new List<BaseItem>();

            foreach (var video in videos)
            {
                if (isElegible(video))
                {
                    elegibleToMerge.Add(video);
                }
            }

            Guid[] ids = new Guid[elegibleToMerge.Count];
            for (int i = 0; i < elegibleToMerge.Count; i++)
            {
                ids[i] = elegibleToMerge.ElementAt(i).Id;
            }

            if (elegibleToMerge.Count() > 1)
            {
                _logger.LogInformation($"Merging {videos.ElementAt(0).Name} ({videos.ElementAt(0).ProductionYear})");
                _logger.LogDebug($"Ids are " + printIds(ids) + " Merging...");
                _videosController.MergeVersions(ids);
                _logger.LogDebug("Merged");
            }
        }

        // Function to print an array of Guids
        private String printIds(Guid[] ids)
        {
            String aux = "";
            foreach (Guid id in ids)
            {
                aux += id;
                aux += " , ";
            }
            return aux;
        }

        // Function to merge TV episodes
        public void MergeEpisodes(IProgress<double> progress)
        {
            var episodes = GetEpisodesFromLibrary().ToArray();

            _logger.LogInformation("Scanning for repeated episodes");

            // Group episodes by series name, season name, episode name, episode number, and year, and select those with more than 1 in the group
            var duplications = episodes.GroupBy(x => new { x.SeriesName, x.SeasonName, x.Name, x.IndexNumber, x.ProductionYear }).Where(x => x.Count() > 1).ToList();

            var total = duplications.Count();
            var current = 0;

            // For each group of duplications, merge the non-merged episodes
            foreach (var e in duplications)
            {
                current++;
                var percent = ((double)current / (double)total) * 100;
                progress?.Report((int)percent);

                _logger.LogInformation($"Merging {e.Key.IndexNumber} ({e.Key.SeriesName})");
                MergeVideos(e.ToList().Where(e => e.PrimaryVersionId == null && e.GetLinkedAlternateVersions().Count() == 0));
            }

            progress?.Report(100);
        }

        // Function to split TV episodes
        public void SplitEpisodes(IProgress<double> progress)
        {
            var episodes = GetEpisodesFromLibrary().ToArray();
            var total = episodes.Count();
            var current = 0;

            // For each TV episode, split it
            foreach (var e in episodes)
            {
                current++;
                var percent = ((double)current / (double)total) * 100;
                progress?.Report((int)percent);

                _logger.LogInformation($"Splitting {e.IndexNumber} ({e.SeriesName})");
                SplitVideo(e);
            }

            progress?.Report(100);
        }

        // Function to check if an item is eligible for merging
        private bool isElegible(BaseItem item)
        {
            if (Plugin.Instance.Configuration.LocationsExcluded != null && Plugin.Instance.Configuration.LocationsExcluded.Any(s => _fileSystem.ContainsSubPath(s, item.Path)))
            {
            _logger.LogInformation($"Item '{item.Name}' ({item.ProductionYear}) located in '{item.Path}' and jellyfin movie id '{item.Id}' is not eligible for merging due to location exclusion.");
            return false;
            }
            // You can add more conditions to determine eligibility here.
            // For example, based on item properties, etc.
    
            _logger.LogInformation($"Item '{item.Name}' ({item.ProductionYear}) located in '{item.Path}' and jellyfin movie id '{item.Id}' is eligible for merging.");
            return true;
        }

        // Placeholder for timer elapsed event handling
        private void OnTimerElapsed()
        {
            // Stop the timer until next update
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public Task RunAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // Dispose of any resources here
        }
    }
}
