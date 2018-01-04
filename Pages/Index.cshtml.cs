using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using ServiceStack.Redis;

namespace cedash.Pages
{
    public class IndexModel : PageModel
    {
        private IMemoryCache _cache;

        public IndexModel(IMemoryCache memoryCache)
        {
            _cache = memoryCache;
        }
        public async Task OnGetAsync()
        {
            double changeLimit = 0.1; // Minimum change over 3 hours to trigger warning
            double volLimit = 1; // Minimum trade volume in BTC last 24 hours

            var client = new System.Net.Http.HttpClient();

            // Grab static(ish) market list from cache, or construct if not found (typically on app startup)
            List<Market> markets;
            var cachedMarkets = _cache.Get("markets");
            if (cachedMarkets == null)
            {
                var jsonMarkets = await client.GetStringAsync("https://www.coinexchange.io/api/v1/getmarkets");
                dynamic jMarkets = JObject.Parse(jsonMarkets);
                IEnumerable<dynamic> marketsResult = jMarkets.result;
                markets = marketsResult.Select(o => new Market()
                {
                    id = o.MarketID.ToString(),
                    name = o.MarketAssetName.ToString(),
                    from = o.MarketAssetCode.ToString(),
                    to = o.BaseCurrencyCode.ToString()
                }).ToList();
                _cache.Set("markets", markets);
            }
            else
            {
                markets = (List<Market>)cachedMarkets;
            }


            // Grab current summary snapshot
            var jsonSummaries = await client.GetStringAsync("https://www.coinexchange.io/api/v1/getmarketsummaries");
            dynamic jSummaries = JObject.Parse(jsonSummaries);
            IEnumerable<dynamic> summariesResult = jSummaries.result;
            List<SummarySnapshot.Summary> summaries = summariesResult.Select(o => new SummarySnapshot.Summary()
            {
                market = o.MarketID.ToString(),
                bid = Double.Parse(o.BidPrice.ToString(), CultureInfo.InvariantCulture),
                ask = Double.Parse(o.AskPrice.ToString(), CultureInfo.InvariantCulture),
                vol = Double.Parse(o.BTCVolume.ToString(), CultureInfo.InvariantCulture),
                last = Double.Parse(o.LastPrice.ToString(), CultureInfo.InvariantCulture),
            })
            .ToList();
            SummarySnapshot snap = new SummarySnapshot() { when = DateTime.Now, summaries = summaries };


            // Grab previous snapshots
            List<SummarySnapshot> snapshots = new List<SummarySnapshot>();
            var cachedSnapshots = _cache.Get("snapshots");
            if (cachedSnapshots != null) snapshots = (List<SummarySnapshot>)cachedSnapshots;
            // Prune old and add current
            snapshots = snapshots.Where(o => o.when > DateTime.Now.AddHours(-3)).ToList();
            snapshots.Add(snap);
            _cache.Set("snapshots", snapshots);


            // Generate fluctuation warnings
            List<Warning> warnings = new List<Warning>();
            double maxDiff = 0;
            foreach (var summary in snap.summaries.Where(o => o.vol > volLimit && o.bid > 0 && o.ask > 0)) // Only with over specified BTC trade volume and non-zero prices
            {
                double lowBid = summary.bid;
                double highBid = summary.bid;
                double lowAsk = summary.ask;
                double highAsk = summary.ask;

                double lastExtremeDiff = 0;
                double lastExtreme = summary.last;

                foreach (var oldSnap in snapshots)
                {
                    var oldMarket = oldSnap.summaries.Where(o => o.market == summary.market).FirstOrDefault();
                    if (oldMarket != null)
                    {
                        if (oldMarket.bid > 0 && oldMarket.ask > 0)
                        {
                            if (oldMarket.bid < lowBid) lowBid = oldMarket.bid;
                            if (oldMarket.bid > highBid) highBid = oldMarket.bid;
                            if (oldMarket.ask < lowAsk) lowAsk = oldMarket.ask;
                            if (oldMarket.ask > highAsk) highAsk = oldMarket.ask;
                            double lastDiff = Math.Abs(summary.last - oldMarket.last) / summary.last;
                            if (lastDiff > lastExtremeDiff)
                            {
                                lastExtremeDiff = lastDiff;
                                lastExtreme = oldMarket.last;
                            }
                        }
                    }
                }

                double lowBidDiff = Math.Abs(summary.bid - lowBid) / summary.bid;
                double highBidDiff = Math.Abs(summary.bid - highBid) / summary.bid;
                double lowAskDiff = Math.Abs(summary.ask - lowAsk) / summary.ask;
                double highAskDiff = Math.Abs(summary.ask - highAsk) / summary.ask;
                if (lowBidDiff > changeLimit || highBidDiff > changeLimit || lowAskDiff > changeLimit || highAskDiff > changeLimit)
                {
                    // Warn
                    Market market = markets.First(o => o.id == summary.market);
                    string label = market.name + " (" + market.from + "/" + market.to + ")";
                    string url = "https://www.coinexchange.io/market/" + market.from + "/" + market.to;
                    bool increase = (lastExtreme >= summary.last);

                    double bid = summary.bid;
                    double ask = summary.ask;

                    double currentMaxDiff = (new[] { lowBidDiff, highBidDiff, lowAskDiff, highAskDiff }).Max();

                    Warning warning = new Warning()
                    {
                        label = label,
                        url = url,
                        increase = increase,
                        bid = summary.bid,
                        ask = summary.ask,
                        lowBid = lowBid,
                        highBid = highBid,
                        lowAsk = lowAsk,
                        highAsk = highAsk,
                        diff = currentMaxDiff
                    };
                    warnings.Add(warning);

                    if (currentMaxDiff > maxDiff) maxDiff = currentMaxDiff;
                }

            }

            ViewData["warnings"] = warnings.OrderByDescending(o => o.diff).ToList();
            ViewData["maxdiff"] = maxDiff;

        }

        [Serializable]
        public class Market
        {
            public string id;
            public string name;
            public string from;
            public string to;
        }

        [Serializable]
        public class SummarySnapshot
        {
            public DateTime when;

            public List<Summary> summaries;

            [Serializable]
            public class Summary
            {
                public string market;

                public double bid;
                public double ask;
                public double vol;
                public double last;
            }
        }

        public class Warning
        {
            public string label;
            public string url;
            public bool increase;
            public double bid;
            public double lowBid;
            public double highBid;
            public double ask;
            public double lowAsk;
            public double highAsk;
            public double diff;
        }
    }
}
