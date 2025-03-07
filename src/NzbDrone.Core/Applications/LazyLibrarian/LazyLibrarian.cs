using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.Applications.LazyLibrarian
{
    public class LazyLibrarian : ApplicationBase<LazyLibrarianSettings>
    {
        public override string Name => "LazyLibrarian";

        private readonly ILazyLibrarianV3Proxy _lazylibrarianV3Proxy;
        private readonly IConfigFileProvider _configFileProvider;

        public LazyLibrarian(ILazyLibrarianV3Proxy lazylibrarianV3Proxy, IConfigFileProvider configFileProvider, IAppIndexerMapService appIndexerMapService, Logger logger)
            : base(appIndexerMapService, logger)
        {
            _lazylibrarianV3Proxy = lazylibrarianV3Proxy;
            _configFileProvider = configFileProvider;
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                failures.AddIfNotNull(_lazylibrarianV3Proxy.TestConnection(Settings));
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Unable to send test message");
                failures.AddIfNotNull(new ValidationFailure("BaseUrl", "Unable to complete application test, cannot connect to LazyLibrarian"));
            }

            return new ValidationResult(failures);
        }

        public override List<AppIndexerMap> GetIndexerMappings()
        {
            var indexers = _lazylibrarianV3Proxy.GetIndexers(Settings);

            var mappings = new List<AppIndexerMap>();

            foreach (var indexer in indexers)
            {
                if (indexer.Apikey == _configFileProvider.ApiKey)
                {
                    var match = AppIndexerRegex.Match(indexer.Host);

                    if (match.Groups["indexer"].Success && int.TryParse(match.Groups["indexer"].Value, out var indexerId))
                    {
                        //Add parsed mapping if it's mapped to a Indexer in this Prowlarr instance
                        mappings.Add(new AppIndexerMap { RemoteIndexerName = $"{indexer.Type},{indexer.Name}", IndexerId = indexerId });
                    }
                }
            }

            return mappings;
        }

        public override void AddIndexer(IndexerDefinition indexer)
        {
            if (indexer.Capabilities.Categories.SupportedCategories(Settings.SyncCategories.ToArray()).Any())
            {
                var lazylibrarianIndexer = BuildLazyLibrarianIndexer(indexer, indexer.Protocol);

                var remoteIndexer = _lazylibrarianV3Proxy.AddIndexer(lazylibrarianIndexer, Settings);
                _appIndexerMapService.Insert(new AppIndexerMap { AppId = Definition.Id, IndexerId = indexer.Id, RemoteIndexerName = $"{remoteIndexer.Type},{remoteIndexer.Name}" });
            }
        }

        public override void RemoveIndexer(int indexerId)
        {
            var appMappings = _appIndexerMapService.GetMappingsForApp(Definition.Id);

            var indexerMapping = appMappings.FirstOrDefault(m => m.IndexerId == indexerId);

            if (indexerMapping != null)
            {
                //Remove Indexer remotely and then remove the mapping
                var indexerProps = indexerMapping.RemoteIndexerName.Split(",");
                _lazylibrarianV3Proxy.RemoveIndexer(indexerProps[1], (LazyLibrarianProviderType)Enum.Parse(typeof(LazyLibrarianProviderType), indexerProps[0]), Settings);
                _appIndexerMapService.Delete(indexerMapping.Id);
            }
        }

        public override void UpdateIndexer(IndexerDefinition indexer)
        {
            _logger.Debug("Updating indexer {0} [{1}]", indexer.Name, indexer.Id);

            var appMappings = _appIndexerMapService.GetMappingsForApp(Definition.Id);
            var indexerMapping = appMappings.FirstOrDefault(m => m.IndexerId == indexer.Id);
            var indexerProps = indexerMapping.RemoteIndexerName.Split(",");

            var lazylibrarianIndexer = BuildLazyLibrarianIndexer(indexer, indexer.Protocol, indexerProps[1]);

            //Use the old remote id to find the indexer on LazyLibrarian incase the update was from a name change in Prowlarr
            var remoteIndexer = _lazylibrarianV3Proxy.GetIndexer(indexerProps[1], lazylibrarianIndexer.Type, Settings);

            if (remoteIndexer != null)
            {
                _logger.Debug("Remote indexer found, syncing with current settings");

                if (!lazylibrarianIndexer.Equals(remoteIndexer))
                {
                    _lazylibrarianV3Proxy.UpdateIndexer(lazylibrarianIndexer, Settings);
                    indexerMapping.RemoteIndexerName = $"{lazylibrarianIndexer.Type},{lazylibrarianIndexer.Altername}";
                    _appIndexerMapService.Update(indexerMapping);
                }
            }
            else
            {
                _appIndexerMapService.Delete(indexerMapping.Id);

                if (indexer.Capabilities.Categories.SupportedCategories(Settings.SyncCategories.ToArray()).Any())
                {
                    _logger.Debug("Remote indexer not found, re-adding {0} to LazyLibrarian", indexer.Name);
                    var newRemoteIndexer = _lazylibrarianV3Proxy.AddIndexer(lazylibrarianIndexer, Settings);
                    _appIndexerMapService.Insert(new AppIndexerMap { AppId = Definition.Id, IndexerId = indexer.Id, RemoteIndexerName = $"{newRemoteIndexer.Type},{newRemoteIndexer.Name}" });
                }
                else
                {
                    _logger.Debug("Remote indexer not found for {0}, skipping re-add to LazyLibrarian due to indexer capabilities", indexer.Name);
                }
            }
        }

        private LazyLibrarianIndexer BuildLazyLibrarianIndexer(IndexerDefinition indexer, DownloadProtocol protocol, string originalName = null)
        {
            var schema = protocol == DownloadProtocol.Usenet ? LazyLibrarianProviderType.Newznab : LazyLibrarianProviderType.Torznab;

            var lazylibrarianIndexer = new LazyLibrarianIndexer
            {
                Name = originalName ?? $"{indexer.Name} (Prowlarr)",
                Altername = $"{indexer.Name} (Prowlarr)",
                Host = $"{Settings.ProwlarrUrl.TrimEnd('/')}/{indexer.Id}/api",
                Apikey = _configFileProvider.ApiKey,
                Categories = string.Join(",", indexer.Capabilities.Categories.SupportedCategories(Settings.SyncCategories.ToArray())),
                Enabled = indexer.Enable,
                Type = schema,
            };

            return lazylibrarianIndexer;
        }
    }
}
