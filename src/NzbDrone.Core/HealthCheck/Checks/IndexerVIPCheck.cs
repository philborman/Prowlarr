using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ProviderAddedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class IndexerVipCheck : HealthCheckBase
    {
        private readonly IIndexerFactory _indexerFactory;

        public IndexerVipCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
        }

        public override HealthCheck Check()
        {
            var enabled = _indexerFactory.Enabled(false);
            var vipProviders = enabled.Where(i => ((IndexerDefinition)i.Definition.Settings).VipExpiration.IsNotNullOrWhiteSpace());
            var expiringProviders = new List<IIndexer>();
            var expiredProviders = new List<IIndexer>();

            foreach (var provider in vipProviders)
            {
                var expiration = ((NewznabSettings)provider.Definition.Settings).VipExpiration;

                if (expiration.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (DateTime.Parse(expiration).Before(DateTime.Now))
                {
                    expiredProviders.Add(provider);
                }

                if (DateTime.Parse(expiration).Between(DateTime.Now, DateTime.Now.AddDays(7)))
                {
                    expiringProviders.Add(provider);
                }
            }

            if (!expiringProviders.Empty())
            {
                return new HealthCheck(GetType(),
                HealthCheckResult.Warning,
                string.Format(_localizationService.GetLocalizedString("IndexerVipCheckExpiringClientMessage"),
                    string.Join(", ", expiringProviders.Select(v => v.Definition.Name))),
                "#vip-expiring");
            }

            if (!expiredProviders.Empty())
            {
                return new HealthCheck(GetType(),
                HealthCheckResult.Warning,
                string.Format(_localizationService.GetLocalizedString("IndexerVipCheckExpiredClientMessage"),
                    string.Join(", ", expiredProviders.Select(v => v.Definition.Name))),
                "#vip-expired");
            }

            return new HealthCheck(GetType());
        }
    }
}
