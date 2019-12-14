﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;

namespace Jackett.Common.Indexers
{
    public class Newpct : BaseCachingWebIndexer
    {
        enum ReleaseType
        {
            TV,
            Movie,
        }

        class NewpctRelease : ReleaseInfo
        {
            public ReleaseType NewpctReleaseType;
            public string SeriesName;
            public int? Season;
            public int? Episode;
            public int? EpisodeTo;
            public int Score;

            public NewpctRelease()
            {
            }

            public NewpctRelease(NewpctRelease copyFrom) :
                base(copyFrom)
            {
                NewpctReleaseType = copyFrom.NewpctReleaseType;
                SeriesName = copyFrom.SeriesName;
                Season = copyFrom.Season;
                Episode = copyFrom.Episode;
                EpisodeTo = copyFrom.EpisodeTo;
                Score = copyFrom.Score;
            }

            public override object Clone()
            {
                return new NewpctRelease(this);
            }
        }

        class DownloadMatcher
        {
            public Regex MatchRegex;
            public MatchEvaluator MatchEvaluator;
        }

        private static Uri DefaultSiteLinkUri =
            new Uri("https://descargas2020.org");

        private static Uri[] ExtraSiteLinkUris = new Uri[]
        {
            new Uri("http://www.tvsinpagar.com/"),
            new Uri("http://torrentlocura.com/"),
            new Uri("https://pctnew.site"),
            new Uri("https://descargas2020.site"),
            new Uri("http://torrentrapid.com/"),
            new Uri("http://tumejortorrent.com/"),
            new Uri("http://pctnew.com/"),
        };

        private static Uri[] LegacySiteLinkUris = new Uri[]
        {
            new Uri("https://pctnew.site"),
            new Uri("http://descargas2020.com/"),
        };

        private NewpctRelease _mostRecentRelease;
        private char[] _wordSeparators = new char[] { ' ', '.', ',', ';', '(', ')', '[', ']', '-', '_' };
        private int _wordNotFoundScore = 100000;
        private Regex _searchStringRegex = new Regex(@"(.+?)S0?(\d+)(E0?(\d+))?$", RegexOptions.IgnoreCase);
        private Regex _titleListRegex = new Regex(@"Serie( *Descargar)?(.+?)(Temporada(.+?)(\d+)(.+?))?Capitulos?(.+?)(\d+)((.+?)(\d+))?(.+?)-(.+?)Calidad(.*)", RegexOptions.IgnoreCase);
        private Regex _titleClassicRegex = new Regex(@"(\[[^\]]*\])?\[Cap\.(\d{1,2})(\d{2})([_-](\d{1,2})(\d{2}))?\]", RegexOptions.IgnoreCase);
        private Regex _titleClassicTvQualityRegex = new Regex(@"\[([^\]]*HDTV[^\]]*)", RegexOptions.IgnoreCase);
        private DownloadMatcher[] _downloadMatchers = new DownloadMatcher[]
        {
            new DownloadMatcher() { MatchRegex = new Regex("([^\"]*/descargar-torrent/[^\"]*)") },
            new DownloadMatcher()
            {
                MatchRegex = new Regex(@"nalt\s*=\s*'([^\/]*)"),
                MatchEvaluator = m => string.Format("/download/{0}.torrent", m.Groups[1])
            },
        };

        private int _maxDailyPages = 7;
        private int _maxMoviesPages = 30;
        private int _maxEpisodesListPages = 100;
        private int[] _allTvCategories = (new TorznabCategory[] { TorznabCatType.TV }).Concat(TorznabCatType.TV.SubCategories).Select(c => c.ID).ToArray();
        private int[] _allMoviesCategories = (new TorznabCategory[] { TorznabCatType.Movies }).Concat(TorznabCatType.Movies.SubCategories).Select(c => c.ID).ToArray();

        private bool _includeVo;
        private bool _filterMovies;
        private bool _removeMovieAccents;
        private DateTime _dailyNow;
        private int _dailyResultIdx;

        private string _searchUrl = "/buscar";
        private string _searchJsonUrl = "/get/result/";
        private string _dailyUrl = "/ultimas-descargas/pg/{0}";
        private string[] _seriesLetterUrls = new string[] { "/series/letter/{0}", "/series-hd/letter/{0}" };
        private string[] _seriesVOLetterUrls = new string[] { "/series-vo/letter/{0}" };
        private string _seriesUrl = "{0}/pg/{1}";
        private string[] _voUrls = new string[] { "serie-vo", "serievo" };

        public override string[] LegacySiteLinks { get; protected set; } = LegacySiteLinkUris.Select(u => u.AbsoluteUri).ToArray();

        public Newpct(IIndexerConfigurationService configService, WebClient wc, Logger l, IProtectionService ps)
            : base(name: "Newpct",
                description: "Newpct - descargar torrent peliculas, series",
                link: DefaultSiteLinkUri.AbsoluteUri,
                caps: new TorznabCapabilities(TorznabCatType.TV,
                                              TorznabCatType.TVSD,
                                              TorznabCatType.TVHD,
                                              TorznabCatType.Movies),
                configService: configService,
                client: wc,
                logger: l,
                p: ps,
                configData: new ConfigurationData())
        {
            Encoding = Encoding.GetEncoding("windows-1252");
            Language = "es-es";
            Type = "public";

            var voItem = new BoolItem() { Name = "Include original versions in search results", Value = false };
            configData.AddDynamic("IncludeVo", voItem);

            var filterMoviesItem = new BoolItem() { Name = "Only full match movies", Value = true };
            configData.AddDynamic("FilterMovies", filterMoviesItem);

            var removeMovieAccentsItem = new BoolItem() { Name = "Remove accents in movie searchs", Value = true };
            configData.AddDynamic("RemoveMovieAccents", removeMovieAccentsItem);
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            configData.LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Count() > 0, () =>
            {
                throw new Exception("Could not find releases from this URL");
            });

            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            Uri link = new Uri(configData.SiteLink.Value);

            lock (cache)
            {
                CleanCache();
            }

            return await PerformQuery(link, query, 0);
        }

        public override async Task<byte[]> Download(Uri linkParam)
        {
            IEnumerable<Uri> uris = GetLinkUris(linkParam);

            foreach (Uri uri in uris)
            {
                byte[] result = null;

                try
                {
                    var results = await RequestStringWithCookiesAndRetry(uri.AbsoluteUri);
                    await FollowIfRedirect(results);
                    var content = results.Content;

                    if (content != null)
                    {
                        Uri uriLink = ExtractDownloadUri(content, uri.AbsoluteUri);
                        if (uriLink != null)
                            result = await base.Download(uriLink);
                    }
                }
                catch
                {
                }

                if (result != null)
                    return result;
                else
                    this.logger.Warn("Newpct - download link not found in " + uri.LocalPath);
            }

            return null;
        }

        private Uri ExtractDownloadUri(string content, string baseLink)
        {
            foreach (DownloadMatcher matcher in _downloadMatchers)
            {
                Match match = matcher.MatchRegex.Match(content);
                if (match.Success)
                {
                    string linkText;

                    if (matcher.MatchEvaluator != null)
                        linkText = (string)matcher.MatchEvaluator.DynamicInvoke(match);
                    else
                        linkText = match.Groups[1].Value;

                    return new Uri(new Uri(baseLink), linkText);
                }
            }

            return null;
        }

        IEnumerable<Uri> GetLinkUris(Uri referenceLink)
        {
            List<Uri> uris = new List<Uri>();
            uris.Add(referenceLink);
            if (DefaultSiteLinkUri.Scheme != referenceLink.Scheme && DefaultSiteLinkUri.Host != referenceLink.Host)
                uris.Add(DefaultSiteLinkUri);

            uris = uris.Concat(ExtraSiteLinkUris.
                Where(u =>
                    (u.Scheme != referenceLink.Scheme || u.Host != referenceLink.Host) &&
                    (u.Scheme != DefaultSiteLinkUri.Scheme || u.Host != DefaultSiteLinkUri.Host))).ToList();

            List<Uri> result = new List<Uri>();

            foreach (Uri uri in uris)
            {
                UriBuilder ub = new UriBuilder(uri);
                ub.Path = referenceLink.LocalPath;
                result.Add(ub.Uri);
            }

            return result;
        }

        private async Task<IEnumerable<ReleaseInfo>> PerformQuery(Uri siteLink, TorznabQuery query, int attempts)
        {
            var releases = new List<ReleaseInfo>();

            _includeVo = ((BoolItem)configData.GetDynamic("IncludeVo")).Value;
            _filterMovies = ((BoolItem)configData.GetDynamic("FilterMovies")).Value;
            _removeMovieAccents = ((BoolItem)configData.GetDynamic("RemoveMovieAccents")).Value;
            _dailyNow = DateTime.Now;
            _dailyResultIdx = 0;
            bool rssMode = string.IsNullOrEmpty(query.SanitizedSearchTerm);

            if (rssMode)
            {
                int pg = 1;
                Uri validUri = null;
                while (pg <= _maxDailyPages)
                {
                    IEnumerable<NewpctRelease> items = null;
                    WebClientStringResult results = null;

                    if (validUri != null)
                    {
                        Uri uri = new Uri(validUri, string.Format(_dailyUrl, pg));
                        results = await RequestStringWithCookiesAndRetry(uri.AbsoluteUri);
                        if (results == null || string.IsNullOrEmpty(results.Content))
                            break;
                        await FollowIfRedirect(results);
                        items = ParseDailyContent(results.Content);
                    }
                    else
                    {
                        foreach (Uri uri in GetLinkUris(new Uri(siteLink, string.Format(_dailyUrl, pg))))
                        {
                            results = await RequestStringWithCookiesAndRetry(uri.AbsoluteUri);
                            if (results != null && !string.IsNullOrEmpty(results.Content))
                            {
                                await FollowIfRedirect(results);
                                items = ParseDailyContent(results.Content);
                                if (items != null && items.Any())
                                {
                                    validUri = uri;
                                    break;
                                }
                            }
                        }
                    }

                    if (items == null || !items.Any())
                        break;

                    releases.AddRange(items);

                    //Check if we need to go to next page
                    bool recentFound = _mostRecentRelease != null &&
                        items.Any(r => r.Title == _mostRecentRelease.Title && r.Link.AbsoluteUri == _mostRecentRelease.Link.AbsoluteUri);
                    if (pg == 1)
                        _mostRecentRelease = (NewpctRelease)items.First().Clone();
                    if (recentFound)
                        break;

                    pg++;
                }
            }
            else
            {
                bool isTvSearch = query.Categories == null || query.Categories.Length == 0 ||
                    query.Categories.Any(c => _allTvCategories.Contains(c));
                if (isTvSearch)
                {
                    releases.AddRange(await TvSearch(siteLink, query));
                }

                bool isMovieSearch = query.Categories == null || query.Categories.Length == 0 ||
                    query.Categories.Any(c => _allMoviesCategories.Contains(c));
                if (isMovieSearch)
                {
                    releases.AddRange(await MovieSearch(siteLink, query));
                }
            }

            return releases;
        }

        private async Task<IEnumerable<ReleaseInfo>> TvSearch(Uri siteLink, TorznabQuery query)
        {
            List<ReleaseInfo> newpctReleases = null;

            string seriesName = query.SanitizedSearchTerm;
            int? season = query.Season > 0 ? (int?)query.Season : null;
            int? episode = null;
            if (!string.IsNullOrWhiteSpace(query.Episode) && int.TryParse(query.Episode, out int episodeTemp))
                episode = episodeTemp;

            //If query has no season/episode info, try to parse title
            if (season == null && episode == null)
            {
                Match searchMatch = _searchStringRegex.Match(query.SanitizedSearchTerm);
                if (searchMatch.Success)
                {
                    seriesName = searchMatch.Groups[1].Value.Trim();
                    season = int.Parse(searchMatch.Groups[2].Value);
                    episode = searchMatch.Groups[4].Success ? (int?)int.Parse(searchMatch.Groups[4].Value) : null;
                }
            }

            //Try to reuse cache
            lock (cache)
            {
                var cachedResult = cache.FirstOrDefault(i => i.Query == seriesName.ToLower());
                if (cachedResult != null)
                    newpctReleases = cachedResult.Results.Select(r => (ReleaseInfo)r.Clone()).ToList();
            }

            if (newpctReleases == null)
            {
                newpctReleases = new List<ReleaseInfo>();

                //Search series url
                foreach (Uri seriesListUrl in SeriesListUris(siteLink, seriesName))
                {
                    newpctReleases.AddRange(await GetReleasesFromUri(seriesListUrl, seriesName));
                }

                //Sonarr removes "the" from shows. If there is nothing try prepending "the"
                if (newpctReleases.Count == 0 && !(seriesName.ToLower().StartsWith("the")))
                {
                    seriesName = "The " + seriesName;
                    foreach (Uri seriesListUrl in SeriesListUris(siteLink, seriesName))
                    {
                        newpctReleases.AddRange(await GetReleasesFromUri(seriesListUrl, seriesName));
                    }
                }

                //Cache ALL episodes
                lock (cache)
                {
                    cache.Add(new CachedQueryResult(seriesName.ToLower(), newpctReleases));
                }
            }

            //Filter only episodes needed
            return newpctReleases.Where(r =>
            {
                NewpctRelease nr = r as NewpctRelease;
                return (
                    nr.Season.HasValue != season.HasValue || //Can't determine if same season
                    nr.Season.HasValue && season.Value == nr.Season.Value && //Same season and ...
                    (
                        nr.Episode.HasValue != episode.HasValue || //Can't determine if same episode
                        nr.Episode.HasValue &&
                        (
                            nr.Episode.Value == episode.Value || //Same episode
                            nr.EpisodeTo.HasValue && episode.Value >= nr.Episode.Value && episode.Value <= nr.EpisodeTo.Value //Episode in interval
                        )
                    )
                );
            });
        }

        private async Task<IEnumerable<ReleaseInfo>> GetReleasesFromUri(Uri uri, string seriesName)
        {
            var newpctReleases = new List<ReleaseInfo>();
            var results = await RequestStringWithCookiesAndRetry(uri.AbsoluteUri);
            await FollowIfRedirect(results);

            //Episodes list
            string seriesEpisodesUrl = ParseSeriesListContent(results.Content, seriesName);
            if (!string.IsNullOrEmpty(seriesEpisodesUrl))
            {
                int pg = 1;
                while (pg < _maxEpisodesListPages)
                {
                    Uri episodesListUrl = new Uri(string.Format(_seriesUrl, seriesEpisodesUrl, pg));
                    results = await RequestStringWithCookiesAndRetry(episodesListUrl.AbsoluteUri);
                    await FollowIfRedirect(results);

                    var items = ParseEpisodesListContent(results.Content);
                    if (items == null || !items.Any())
                        break;

                    newpctReleases.AddRange(items);

                    pg++;
                }
            }
            return newpctReleases;
        }

        private IEnumerable<Uri> SeriesListUris(Uri siteLink, string seriesName)
        {
            IEnumerable<string> lettersUrl;
            if (!_includeVo)
            {
                lettersUrl = _seriesLetterUrls;
            }
            else
            {
                lettersUrl = _seriesLetterUrls.Concat(_seriesVOLetterUrls);
            }
            string seriesLetter = !char.IsDigit(seriesName[0]) ? seriesName[0].ToString() : "0-9";
            return lettersUrl.Select(urlFormat =>
            {
                return new Uri(siteLink, string.Format(urlFormat, seriesLetter.ToLower()));
            });
        }

        private IEnumerable<NewpctRelease> ParseDailyContent(string content)
        {
            var SearchResultParser = new HtmlParser();
            var doc = SearchResultParser.ParseDocument(content);

            List<NewpctRelease> releases = new List<NewpctRelease>();

            try
            {
                var rows = doc.QuerySelectorAll(".content .info");
                foreach (var row in rows)
                {
                    var anchor = row.QuerySelector("a");
                    var title = Regex.Replace(anchor.TextContent, @"\s+", " ").Trim();
                    var title2 = Regex.Replace(anchor.GetAttribute("title"), @"\s+", " ").Trim();
                    if (title2.Length >= title.Length)
                        title = title2;

                    var detailsUrl = anchor.GetAttribute("href");
                    if (!_includeVo && _voUrls.Any(vo => detailsUrl.ToLower().Contains(vo.ToLower())))
                        continue;

                    var span = row.QuerySelector("span");
                    var quality = span.ChildNodes[0].TextContent.Trim();
                    ReleaseType releaseType = ReleaseTypeFromQuality(quality);
                    var sizeText = span.ChildNodes[1].TextContent.Replace("Tama\u00F1o", "").Trim();

                    var div = row.QuerySelector("div");
                    var language = div.ChildNodes[1].TextContent.Trim();
                    _dailyResultIdx++;

                    NewpctRelease newpctRelease;
                    if (releaseType == ReleaseType.TV)
                        newpctRelease = GetReleaseFromData(releaseType,
                        string.Format("Serie {0} - {1} Calidad [{2}]", title, language, quality),
                        detailsUrl, quality, language, ReleaseInfo.GetBytes(sizeText), _dailyNow - TimeSpan.FromMilliseconds(_dailyResultIdx));
                    else
                        newpctRelease = GetReleaseFromData(releaseType,
                        string.Format("{0} [{1}][{2}]", title, quality, language),
                        detailsUrl, quality, language, ReleaseInfo.GetBytes(sizeText), _dailyNow - TimeSpan.FromMilliseconds(_dailyResultIdx));

                    releases.Add(newpctRelease);
                }
            }
            catch (Exception ex)
            {
                OnParseError(content, ex);
            }

            return releases;
        }

        private string ParseSeriesListContent(string content, string title)
        {
            var SearchResultParser = new HtmlParser();
            var doc = SearchResultParser.ParseDocument(content);

            Dictionary<string, string> results = new Dictionary<string, string>();

            try
            {
                var rows = doc.QuerySelectorAll(".pelilist li a");
                foreach (var anchor in rows)
                {
                    var h2 = anchor.QuerySelector("h2");
                    if (h2.TextContent.Trim().ToLower() == title.Trim().ToLower())
                        return anchor.GetAttribute("href");
                }
            }
            catch (Exception ex)
            {
                OnParseError(content, ex);
            }

            return null;
        }

        private IEnumerable<NewpctRelease> ParseEpisodesListContent(string content)
        {
            var SearchResultParser = new HtmlParser();
            var doc = SearchResultParser.ParseDocument(content);

            List<NewpctRelease> releases = new List<NewpctRelease>();

            try
            {
                var rows = doc.QuerySelectorAll(".content .info");
                foreach (var row in rows)
                {
                    var anchor = row.QuerySelector("a");
                    var title = anchor.TextContent.Replace("\t", "").Trim();
                    var detailsUrl = anchor.GetAttribute("href");

                    var span = row.QuerySelector("span");
                    var pubDateText = row.ChildNodes[3].TextContent.Trim();
                    var sizeText = row.ChildNodes[5].TextContent.Trim();

                    long size = ReleaseInfo.GetBytes(sizeText);
                    DateTime publishDate = DateTime.ParseExact(pubDateText, "dd-MM-yyyy", null);
                    NewpctRelease newpctRelease = GetReleaseFromData(ReleaseType.TV, title, detailsUrl, null, null, size, publishDate);

                    releases.Add(newpctRelease);
                }
            }
            catch (Exception ex)
            {
                OnParseError(content, ex);
            }

            return releases;
        }

        private async Task<IEnumerable<ReleaseInfo>> MovieSearch(Uri siteLink, TorznabQuery query)
        {
            var releases = new List<NewpctRelease>();

            string searchStr = query.SanitizedSearchTerm;
            if (_removeMovieAccents)
                searchStr = RemoveDiacritics(searchStr);

            Uri validUri = null;
            bool validUriUsesJson = false;
            int pg = 1;
            while (pg <= _maxMoviesPages)
            {
                var queryCollection = new Dictionary<string, string>();
                queryCollection.Add("q", searchStr);
                queryCollection.Add("s", searchStr);
                queryCollection.Add("pg", pg.ToString());

                WebClientStringResult results = null;
                IEnumerable<NewpctRelease> items = null;

                if (validUri != null)
                {
                    if (validUriUsesJson)
                    {
                        Uri uri = new Uri(validUri, _searchJsonUrl);
                        results = await PostDataWithCookies(uri.AbsoluteUri, queryCollection);
                        if (results == null || string.IsNullOrEmpty(results.Content))
                            break;
                        items = ParseSearchJsonContent(uri, results.Content);
                    }
                    else
                    {
                        Uri uri = new Uri(validUri, _searchUrl);
                        results = await PostDataWithCookies(uri.AbsoluteUri, queryCollection);
                        if (results == null || string.IsNullOrEmpty(results.Content))
                            break;
                        items = ParseSearchContent(results.Content);
                    }
                }
                else
                {
                    using (var jsonUris = GetLinkUris(new Uri(siteLink, _searchJsonUrl)).GetEnumerator())
                    {
                        using (var uris = GetLinkUris(new Uri(siteLink, _searchUrl)).GetEnumerator())
                        {
                            bool resultFound = false;
                            while (jsonUris.MoveNext() && uris.MoveNext() && !resultFound)
                            {
                                for (int i = 0; i < 2 && !resultFound; i++)
                                {
                                    bool usingJson = i == 0;

                                    Uri uri;
                                    if (usingJson)
                                        uri = jsonUris.Current;
                                    else
                                        uri = uris.Current;

                                    try
                                    {
                                        results = await PostDataWithCookies(uri.AbsoluteUri, queryCollection);
                                    }
                                    catch
                                    {
                                        results = null;
                                    }

                                    if (results != null && !string.IsNullOrEmpty(results.Content))
                                    {
                                        if (usingJson)
                                            items = ParseSearchJsonContent(uri, results.Content);
                                        else
                                            items = ParseSearchContent(results.Content);

                                        if (items != null)
                                        {
                                            validUri = uri;
                                            validUriUsesJson = usingJson;
                                            resultFound = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (items == null)
                    break;

                releases.AddRange(items);
                pg++;
            }

            ScoreReleases(releases, searchStr);

            if (_filterMovies)
                releases = releases.Where(r => r.Score < _wordNotFoundScore).ToList();

            return releases;
        }

        private IEnumerable<NewpctRelease> ParseSearchContent(string content)
        {
            bool someFound = false;
            var SearchResultParser = new HtmlParser();
            var doc = SearchResultParser.ParseDocument(content);

            List<NewpctRelease> releases = new List<NewpctRelease>();

            try
            {
                var rows = doc.QuerySelectorAll(".content .info");
                if (rows == null || !rows.Any())
                    return null;
                foreach (var row in rows)
                {
                    var anchor = row.QuerySelector("a");
                    var h2 = anchor.QuerySelector("h2");
                    var title = Regex.Replace(h2.TextContent, @"\s+", " ").Trim();
                    var detailsUrl = anchor.GetAttribute("href");

                    someFound = true;

                    bool isSeries = h2.QuerySelector("span") != null && h2.TextContent.ToLower().Contains("calidad");
                    bool isGame = title.ToLower().Contains("pcdvd");
                    if (isSeries || isGame)
                        continue;

                    var span = row.QuerySelectorAll("span");

                    var pubDateText = span[1].TextContent.Trim();
                    var sizeText = span[2].TextContent.Trim();

                    long size = 0;
                    try
                    {
                        size = ReleaseInfo.GetBytes(sizeText);
                    }
                    catch
                    {
                    }
                    DateTime publishDate;
                    DateTime.TryParseExact(pubDateText, "dd-MM-yyyy", null, DateTimeStyles.None, out publishDate);

                    var div = row.QuerySelector("div");

                    NewpctRelease newpctRelease;
                    newpctRelease = GetReleaseFromData(ReleaseType.Movie, title, detailsUrl, null, null, size, publishDate);

                    releases.Add(newpctRelease);
                }
            }
            catch (Exception ex)
            {
                return null;
            }

            if (!someFound)
                return null;

            return releases;
        }

        private IEnumerable<NewpctRelease> ParseSearchJsonContent(Uri uri, string content)
        {
            bool someFound = false;

            List<NewpctRelease> releases = new List<NewpctRelease>();

            //Remove path from uri
            UriBuilder ub = new UriBuilder(uri);
            ub.Path = string.Empty;
            uri = ub.Uri;

            try
            {
                var jo = JObject.Parse(content);

                int numItems = int.Parse(jo["data"]["items"].ToString());
                for (int i = 0; i < numItems; i++)
                {
                    var item = jo["data"]["torrents"]["0"][i.ToString()];

                    string url = item["guid"].ToString();
                    string title = item["torrentName"].ToString();
                    string pubDateText = item["torrentDateAdded"].ToString();
                    string calidad = item["calidad"].ToString();
                    string sizeText = item["torrentSize"].ToString();

                    someFound = true;

                    bool isSeries = calidad != null && calidad.ToLower().Contains("hdtv");
                    bool isGame = title.ToLower().Contains("pcdvd");
                    if (isSeries || isGame)
                        continue;

                    long size = 0;
                    try
                    {
                        size = ReleaseInfo.GetBytes(sizeText);
                    }
                    catch
                    {
                    }
                    DateTime publishDate;
                    DateTime.TryParseExact(pubDateText, "dd/MM/yyyy", null, DateTimeStyles.None, out publishDate);

                    NewpctRelease newpctRelease;
                    string detailsUrl = new Uri(uri, url).AbsoluteUri;
                    newpctRelease = GetReleaseFromData(ReleaseType.Movie, title, detailsUrl, calidad, null, size, publishDate);

                    releases.Add(newpctRelease);

                }


            }
            catch (Exception ex)
            {
                return null;
            }

            if (!someFound)
                return null;

            return releases;
        }

        private void ScoreReleases(IEnumerable<NewpctRelease> releases, string searchTerm)
        {
            string[] searchWords = searchTerm.ToLower().Split(_wordSeparators, StringSplitOptions.None).
                Select(s => s.Trim()).
                Where(s => !string.IsNullOrEmpty(s)).ToArray();

            foreach (NewpctRelease release in releases)
            {
                release.Score = 0;
                string[] releaseWords = release.Title.ToLower().Split(_wordSeparators, StringSplitOptions.None).
                    Select(s => s.Trim()).
                    Where(s => !string.IsNullOrEmpty(s)).ToArray();

                foreach (string search in searchWords)
                {
                    int index = Array.IndexOf(releaseWords, search);
                    if (index >= 0)
                    {
                        release.Score += index;
                        releaseWords[index] = null;
                    }
                    else
                    {
                        release.Score += _wordNotFoundScore;
                    }
                }
            }
        }

        ReleaseType ReleaseTypeFromQuality(string quality)
        {
            if (quality.Trim().ToLower().StartsWith("hdtv"))
                return ReleaseType.TV;
            else
                return ReleaseType.Movie;
        }

        NewpctRelease GetReleaseFromData(ReleaseType releaseType, string title, string detailsUrl, string quality, string language, long size, DateTime publishDate)
        {
            NewpctRelease result = new NewpctRelease();
            result.NewpctReleaseType = releaseType;

            //Sanitize
            title = title.Replace("\t", "").Replace("\x2013", "-");

            Match match = _titleListRegex.Match(title);
            if (match.Success)
            {
                result.SeriesName = match.Groups[2].Value.Trim(' ', '-');
                result.Season = int.Parse(match.Groups[5].Success ? match.Groups[5].Value.Trim() : "1");
                result.Episode = int.Parse(match.Groups[8].Value.Trim().PadLeft(2, '0'));
                result.EpisodeTo = match.Groups[11].Success ? (int?)int.Parse(match.Groups[11].Value.Trim()) : null;
                string audioQuality = match.Groups[13].Value.Trim(' ', '[', ']');
                if (string.IsNullOrEmpty(language))
                    language = audioQuality;
                quality = match.Groups[14].Value.Trim(' ', '[', ']');

                string seasonText = result.Season.ToString();
                string episodeText = seasonText + result.Episode.ToString().PadLeft(2, '0');
                string episodeToText = result.EpisodeTo.HasValue ? "_" + seasonText + result.EpisodeTo.ToString().PadLeft(2, '0') : "";

                result.Title = string.Format("{0} - Temporada {1} [{2}][Cap.{3}{4}][{5}]",
                    result.SeriesName, seasonText, quality, episodeText, episodeToText, audioQuality);
            }
            else
            {
                Match matchClassic = _titleClassicRegex.Match(title);
                if (matchClassic.Success)
                {
                    result.Season = matchClassic.Groups[2].Success ? (int?)int.Parse(matchClassic.Groups[2].Value) : null;
                    result.Episode = matchClassic.Groups[3].Success ? (int?)int.Parse(matchClassic.Groups[3].Value) : null;
                    result.EpisodeTo = matchClassic.Groups[6].Success ? (int?)int.Parse(matchClassic.Groups[6].Value) : null;
                    if (matchClassic.Groups[1].Success)
                        quality = matchClassic.Groups[1].Value;
                }

                result.Title = title;
            }

            if (releaseType == ReleaseType.TV)
            {
                if (!string.IsNullOrWhiteSpace(quality) && (quality.Contains("720") || quality.Contains("1080")))
                    result.Category = new List<int> { TorznabCatType.TVHD.ID };
                else
                    result.Category = new List<int> { TorznabCatType.TV.ID };
            }
            else
            {
                result.Title = title;
                result.Category = new List<int> { TorznabCatType.Movies.ID };
            }

            if (size > 0)
                result.Size = size;
            result.Link = new Uri(detailsUrl);
            result.Guid = result.Link;
            result.Comments = result.Link;
            result.PublishDate = publishDate;
            result.Seeders = 1;
            result.Peers = 1;

            result.Title = FixedTitle(result, quality, language);
            result.DownloadVolumeFactor = 0;
            result.UploadVolumeFactor = 1;

            return result;
        }

        private string FixedTitle(NewpctRelease release, string quality, string language)
        {
            if (String.IsNullOrEmpty(release.SeriesName))
            {
                release.SeriesName = release.Title;
                if (release.NewpctReleaseType == ReleaseType.TV && release.SeriesName.Contains("-"))
                    release.SeriesName = release.Title.Substring(0, release.SeriesName.IndexOf('-') - 1);
            }

            var titleParts = new List<string>();

            titleParts.Add(release.SeriesName);

            if (release.NewpctReleaseType == ReleaseType.TV)
            {
                if (String.IsNullOrEmpty(quality))
                    quality = "HDTV";

                var seasonAndEpisode = "S" + release.Season.ToString().PadLeft(2, '0');
                seasonAndEpisode += "E" + release.Episode.ToString().PadLeft(2, '0');
                if (release.EpisodeTo != release.Episode && release.EpisodeTo != null && release.EpisodeTo != 0)
                {
                    seasonAndEpisode += "-" + release.EpisodeTo.ToString().PadLeft(2, '0');
                }
                titleParts.Add(seasonAndEpisode);
            }

            if (!string.IsNullOrEmpty(quality) && !release.SeriesName.Contains(quality))
            {
                titleParts.Add(quality);
            }

            if (!string.IsNullOrWhiteSpace(language) && !release.SeriesName.Contains(language))
            {
                titleParts.Add(language);
            }

            if (release.Title.ToLower().Contains("espa\u00F1ol") ||
                release.Title.ToLower().Contains("espanol") ||
                release.Title.ToLower().Contains("castellano") ||
                release.Title.ToLower().EndsWith("espa"))
            {
                titleParts.Add("Spanish");
            }

            string result = String.Join(".", titleParts);

            result = Regex.Replace(result, @"[\[\]]+", ".");
            result = Regex.Replace(result, @"\.[ \.]*\.", ".");

            return result;
        }

        private string RemoveDiacritics(string text)
        {
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
