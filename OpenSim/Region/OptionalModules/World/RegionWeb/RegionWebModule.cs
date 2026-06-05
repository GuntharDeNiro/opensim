/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.World.RegionWeb
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionWebModule")]
    public class RegionWebModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const int InventoryCarouselFolderSearchLimit = 1024;
        private const string ScriptEngineFeatureTitle = "Second Life-style script engine";
        private const string ScriptEngineFeatureBody =
            "The script engine is moving closer to Second Life behavior with Experience-Lite permissions, scripted sit controls, key-value stores, linkset data, environment, estate-return, parcel media, parcel prim counts/details, guarded money transfer, inventory transfer, damage, RSA, attachment filter, identity lookup, privacy-aware agent language lookup, animation-state introspection, physics energy readback, object-detail cost readback, script memory/profiler diagnostics, GLTF material and physics primitive-param helpers, plus a Vanilla Sim compatibility center and in-world regression lab.";
        private const string ScriptEngineFeatureOverview =
            "The script engine now includes a wider Second Life-style scripting surface for modern estate systems. Trusted estate scripts can use Experience-Lite permissions, persistent experience key-value storage, linkset data with linkset_data events, scripted sit controls, linked sound controls, region and parcel environment helpers, estate return and terrain helpers, parcel media controls, same-owner simulator-wide parcel prim counts/details, guarded debit-permission money transfer, direct inventory and ownership transfer, direct damage helpers with Combat2-style pre-application damage transactions, cached identity lookup, privacy-aware agent language lookup, animation-state introspection, physics energy readback, object-detail cost/render/selection readback, script memory limit/profiler diagnostics, GLTF/render material primitive params with stored override readback, physics material primitive params, secure hashing/HMAC/RSA helpers, parameterized rez/derez workflows, filtered attachment inspection and HUD coordinate helpers without relying on brittle scripted workarounds. Second Life pathfinding character calls now provide persistent character option state, baked terrain navmesh caching, terrain-aware A* routing, parcel-stay handling and dynamic object/avatar obstacle avoidance where OpenSim does not expose the proprietary SL navmesh service. Vanilla Sim exposes a script compatibility center, and the example suite includes an in-world regression controller for post-build checks.";
        private const string CurrencyFeatureTitle = "Viewer-visible local currency";
        private const string CurrencyFeatureBody =
            "The estate can run a local persistent currency ledger that sends live balances to the viewer, handles transfers, object payments, land/object purchases and simulator economy charges without requiring a separate currency server.";
        private const string CurrencyFeatureOverview =
            "The bundled BetaGridLikeMoneyModule now works as a lightweight local economy backend. It persists avatar balances in a tab-separated ledger, grants a configurable first-use balance, pushes MoneyBalanceReply updates so compatible viewers show the current balance, and applies the same balance path to viewer transfers, scripted money calls, object payments, land/object purchases, upload charges and group creation charges.";
        private const string MultiGridFeatureTitle = "Multi-grid region attachments";
        private const string MultiGridFeatureBody =
            "Regions can keep one primary grid for identity, assets, inventory and presence while publishing their simulator endpoint to additional grid registries as configured secondary attachments.";
        private const string MultiGridFeatureOverview =
            "The region grid connector can now fan out successful primary region registrations to a configurable list of secondary grid services. Each attachment can target all regions or selected region names/UUIDs, override the advertised DNS name, HTTP endpoint, map location and region name, and use optional HTTP Basic authentication. Secondary attachments are best-effort by default so a public grid outage does not stop the simulator from booting.";
        private const string VanillaSimRepositoryUrl = "https://github.com/GuntharDeNiro/opensim";

        private readonly object m_sync = new object();
        private readonly Dictionary<UUID, Scene> m_scenesByID = new Dictionary<UUID, Scene>();
        private readonly Dictionary<string, UUID> m_regionIDsBySlug = new Dictionary<string, UUID>(StringComparer.OrdinalIgnoreCase);
        private const string CurrencySessionCookie = "RegionWebCurrency";
        private readonly object m_currencyAuthLock = new object();
        private readonly object m_currencyPurchaseLock = new object();
        private readonly object m_currencyPayPalLock = new object();
        private readonly object m_inventoryCarouselCacheLock = new object();
        private readonly Dictionary<string, CurrencyLoginChallenge> m_currencyChallenges = new Dictionary<string, CurrencyLoginChallenge>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CurrencyWebSession> m_currencySessions = new Dictionary<string, CurrencyWebSession>(StringComparer.Ordinal);
        private readonly Dictionary<UUID, DateTime> m_currencyLastChallengeUTCByAgent = new Dictionary<UUID, DateTime>();
        private readonly Dictionary<string, CurrencyPurchaseRequest> m_currencyPurchaseRequests = new Dictionary<string, CurrencyPurchaseRequest>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CurrencyPayPalOrder> m_currencyPayPalOrders = new Dictionary<string, CurrencyPayPalOrder>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<UUID, InventoryCarouselAssetCacheEntry> m_inventoryCarouselAssetCache = new Dictionary<UUID, InventoryCarouselAssetCacheEntry>();

        private bool m_enabled;
        private bool m_handlerRegistered;
        private bool m_autoCreateContent;
        private bool m_showMap;
        private bool m_showStats;
        private bool m_showParcels;
        private bool m_inventoryCarouselEnabled = true;
        private bool m_currencyPortalEnabled = true;
        private bool m_currencyBuyEnabled = true;
        private bool m_currencyTransferEnabled = true;
        private bool m_payPalEnabled;
        private int m_postsPerPage;
        private int m_currencyChallengeMinutes = 10;
        private int m_currencyChallengeCooldownSeconds = 20;
        private int m_currencySessionHours = 12;
        private int m_currencyStatementLimit = 30;
        private int m_currencyBuyLimit = 100000;
        private int m_inventoryCarouselLimit = 12;
        private int m_inventoryCarouselCacheSeconds = 300;
        private string m_defaultEstateTitle = "Vanilla Sim";
        private string m_basePath = "/regionweb";
        private string m_contentDirectory = "RegionWeb";
        private string m_inventoryCarouselFolder = "RegionWeb Carousel";
        private string m_regionInventoryCarouselFolderTemplate = "RegionWeb {RegionName} Carousel";
        private string m_currencyBuyMode = "grant";
        private string m_currencyPurchaseStoragePath = "Currency/regionweb-purchases.tsv";
        private string m_payPalEnvironment = "sandbox";
        private string m_payPalClientID = string.Empty;
        private string m_payPalClientSecret = string.Empty;
        private string m_payPalCurrencyCode = "EUR";
        private string m_payPalReturnBaseUrl = string.Empty;
        private string m_payPalOrderStoragePath = "Currency/regionweb-paypal-orders.tsv";
        private string m_absoluteContentDirectory;
        private string m_absoluteCurrencyPurchaseStoragePath;
        private string m_absolutePayPalOrderStoragePath;
        private decimal m_payPalPricePerToken = 0.01m;

        public string Name { get { return "RegionWebModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["RegionWeb"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            m_basePath = CleanPath(config.GetString("PublicPath", "/regionweb"));
            m_contentDirectory = config.GetString("ContentDirectory", "RegionWeb").Trim();
            m_autoCreateContent = config.GetBoolean("AutoCreateContent", true);
            m_showMap = config.GetBoolean("ShowMap", true);
            m_showStats = config.GetBoolean("ShowStats", true);
            m_showParcels = config.GetBoolean("ShowParcels", true);
            m_postsPerPage = Math.Max(1, config.GetInt("PostsPerPage", 5));
            m_inventoryCarouselEnabled = config.GetBoolean("InventoryCarouselEnabled", true);
            m_inventoryCarouselFolder = config.GetString("InventoryCarouselFolder", "RegionWeb Carousel").Trim();
            m_regionInventoryCarouselFolderTemplate = config.GetString("RegionInventoryCarouselFolderTemplate", "RegionWeb {RegionName} Carousel").Trim();
            m_inventoryCarouselLimit = Math.Max(1, config.GetInt("InventoryCarouselLimit", 12));
            m_inventoryCarouselCacheSeconds = Math.Max(0, config.GetInt("InventoryCarouselCacheSeconds", 300));
            m_currencyPortalEnabled = config.GetBoolean("CurrencyPortalEnabled", true);
            m_currencyBuyEnabled = config.GetBoolean("CurrencyBuyEnabled", true);
            m_currencyTransferEnabled = config.GetBoolean("CurrencyTransferEnabled", true);
            m_currencyChallengeMinutes = Math.Max(1, config.GetInt("CurrencyChallengeMinutes", 10));
            m_currencyChallengeCooldownSeconds = Math.Max(0, config.GetInt("CurrencyChallengeCooldownSeconds", 20));
            m_currencySessionHours = Math.Max(1, config.GetInt("CurrencySessionHours", 12));
            m_currencyStatementLimit = Math.Max(1, config.GetInt("CurrencyStatementLimit", 30));
            m_currencyBuyLimit = Math.Max(1, config.GetInt("CurrencyBuyLimit", 100000));
            m_currencyBuyMode = NormalizeCurrencyBuyMode(config.GetString("CurrencyBuyMode", "grant"));
            m_currencyPurchaseStoragePath = config.GetString("CurrencyPurchaseStorage", "Currency/regionweb-purchases.tsv").Trim();
            m_payPalEnabled = config.GetBoolean("PayPalEnabled", false);
            m_payPalEnvironment = NormalizePayPalEnvironment(config.GetString("PayPalEnvironment", "sandbox"));
            m_payPalClientID = config.GetString("PayPalClientID", string.Empty).Trim();
            m_payPalClientSecret = config.GetString("PayPalClientSecret", string.Empty).Trim();
            m_payPalCurrencyCode = NormalizePayPalCurrency(config.GetString("PayPalCurrencyCode", "EUR"));
            m_payPalPricePerToken = ParsePositiveDecimal(config.GetString("PayPalPricePerToken", "0.01"), 0.01m);
            m_payPalReturnBaseUrl = config.GetString("PayPalReturnBaseUrl", string.Empty).Trim();
            m_payPalOrderStoragePath = config.GetString("PayPalOrderStorage", "Currency/regionweb-paypal-orders.tsv").Trim();
            m_defaultEstateTitle = config.GetString("EstateTitle", "Vanilla Sim").Trim();
            if (string.IsNullOrEmpty(m_defaultEstateTitle))
                m_defaultEstateTitle = "Vanilla Sim";

            if (string.IsNullOrEmpty(m_contentDirectory))
                m_contentDirectory = "RegionWeb";
            if (string.IsNullOrEmpty(m_inventoryCarouselFolder))
                m_inventoryCarouselFolder = "RegionWeb Carousel";
            if (string.IsNullOrEmpty(m_regionInventoryCarouselFolderTemplate))
                m_regionInventoryCarouselFolderTemplate = "RegionWeb {RegionName} Carousel";
            if (string.IsNullOrEmpty(m_currencyPurchaseStoragePath))
                m_currencyPurchaseStoragePath = "Currency/regionweb-purchases.tsv";
            if (string.IsNullOrEmpty(m_payPalOrderStoragePath))
                m_payPalOrderStoragePath = "Currency/regionweb-paypal-orders.tsv";

            m_absoluteContentDirectory = Path.IsPathRooted(m_contentDirectory)
                ? m_contentDirectory
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_contentDirectory);
            m_absoluteCurrencyPurchaseStoragePath = Path.IsPathRooted(m_currencyPurchaseStoragePath)
                ? m_currencyPurchaseStoragePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_currencyPurchaseStoragePath);
            m_absolutePayPalOrderStoragePath = Path.IsPathRooted(m_payPalOrderStoragePath)
                ? m_payPalOrderStoragePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_payPalOrderStoragePath);
        }

        public void PostInitialise()
        {
            if (!m_enabled)
                return;

            try
            {
                Directory.CreateDirectory(m_absoluteContentDirectory);
                if (m_autoCreateContent)
                    EnsureEstateContent();
                LoadCurrencyPurchaseRequests();
                LoadCurrencyPayPalOrders();

                IHttpServer server = MainServer.GetHttpServer(0);
                server.AddSimpleStreamHandler(new SimpleStreamHandler(m_basePath, HandleRequest, "RegionWeb"));
                server.AddSimpleStreamHandler(new SimpleStreamHandler(m_basePath, HandleRequest, "RegionWeb"), true);
                m_handlerRegistered = true;

                MainConsole.Instance.Commands.AddCommand(
                    "RegionWeb", false, "regionweb show",
                    "regionweb show",
                    "Show public Vanilla Sim web URLs and content folders for loaded regions.",
                    HandleShowCommand);

                MainConsole.Instance.Commands.AddCommand(
                    "RegionWeb", false, "regionweb currency pending",
                    "regionweb currency pending",
                    "List pending Vanilla Sim wallet token purchase requests.",
                    HandleCurrencyCommand);

                MainConsole.Instance.Commands.AddCommand(
                    "RegionWeb", false, "regionweb currency approve",
                    "regionweb currency approve <request-id> [note]",
                    "Approve a pending Vanilla Sim wallet token purchase request and credit the avatar.",
                    HandleCurrencyCommand);

                MainConsole.Instance.Commands.AddCommand(
                    "RegionWeb", false, "regionweb currency deny",
                    "regionweb currency deny <request-id> [note]",
                    "Deny a pending Vanilla Sim wallet token purchase request.",
                    HandleCurrencyCommand);

                m_log.InfoFormat("[REGION WEB]: Enabled at {0}; content folder {1}", m_basePath, m_absoluteContentDirectory);
            }
            catch (Exception e)
            {
                m_enabled = false;
                m_log.WarnFormat("[REGION WEB]: Could not enable module: {0}", e.Message);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            AddOrUpdateScene(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            AddOrUpdateScene(scene);

            if (m_autoCreateContent)
                EnsureRegionContent(scene);

            EnsureInventoryCarouselFolders(scene);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_sync)
            {
                m_scenesByID.Remove(scene.RegionInfo.RegionID);

                List<string> deadSlugs = new List<string>();
                foreach (KeyValuePair<string, UUID> kvp in m_regionIDsBySlug)
                {
                    if (kvp.Value == scene.RegionInfo.RegionID)
                        deadSlugs.Add(kvp.Key);
                }

                foreach (string slug in deadSlugs)
                    m_regionIDsBySlug.Remove(slug);
            }
        }

        public void Close()
        {
            if (m_handlerRegistered)
            {
                MainServer.GetHttpServer(0).RemoveSimpleStreamHandler(m_basePath);
                MainServer.GetHttpServer(0).RemoveSimpleStreamHandler(m_basePath);
                m_handlerRegistered = false;
            }

            lock (m_sync)
            {
                m_scenesByID.Clear();
                m_regionIDsBySlug.Clear();
            }

            lock (m_currencyAuthLock)
            {
                m_currencyChallenges.Clear();
                m_currencySessions.Clear();
                m_currencyLastChallengeUTCByAgent.Clear();
            }

            lock (m_currencyPurchaseLock)
                m_currencyPurchaseRequests.Clear();

            lock (m_currencyPayPalLock)
                m_currencyPayPalOrders.Clear();

            lock (m_inventoryCarouselCacheLock)
                m_inventoryCarouselAssetCache.Clear();
        }

        private void AddOrUpdateScene(Scene scene)
        {
            string slug = MakeSlug(scene.RegionInfo.RegionName);

            lock (m_sync)
            {
                m_scenesByID[scene.RegionInfo.RegionID] = scene;
                m_regionIDsBySlug[slug] = scene.RegionInfo.RegionID;
                m_regionIDsBySlug[scene.RegionInfo.RegionID.ToString()] = scene.RegionInfo.RegionID;
            }
        }

        private void HandleShowCommand(string module, string[] cmd)
        {
            List<Scene> scenes;
            lock (m_sync)
                scenes = new List<Scene>(m_scenesByID.Values);

            if (scenes.Count == 0)
            {
                MainConsole.Instance.Output("[REGION WEB]: No loaded regions.");
                return;
            }

            foreach (Scene scene in scenes.OrderBy(s => s.RegionInfo.RegionName))
            {
                string slug = MakeSlug(scene.RegionInfo.RegionName);
                MainConsole.Instance.Output(
                    "[REGION WEB]: {0}: {1}{2}/{3}/  content: {4}",
                    scene.RegionInfo.RegionName,
                    scene.RegionInfo.ServerURI,
                    m_basePath.TrimStart('/'),
                    slug,
                    GetRegionDirectory(scene));
            }
        }

        private void HandleCurrencyCommand(string module, string[] cmd)
        {
            if (cmd == null || cmd.Length < 3)
            {
                MainConsole.Instance.Output("[REGION WEB]: Usage: regionweb currency pending|approve|deny");
                return;
            }

            string action = cmd[2].ToLowerInvariant();
            if (action == "pending")
            {
                List<CurrencyPurchaseRequest> pending;
                lock (m_currencyPurchaseLock)
                    pending = m_currencyPurchaseRequests.Values
                        .Where(r => (r.Status ?? string.Empty).Equals("pending", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(r => r.RequestedUTC)
                        .ToList();

                if (pending.Count == 0)
                {
                    MainConsole.Instance.Output("[REGION WEB]: No pending currency purchase requests.");
                    return;
                }

                foreach (CurrencyPurchaseRequest request in pending)
                {
                    MainConsole.Instance.Output(
                        "[REGION WEB]: {0}: {1} requested {2} tokens at {3}",
                        request.RequestID,
                        request.DisplayName,
                        request.Amount.ToString(CultureInfo.InvariantCulture),
                        request.RequestedUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture));
                }
                return;
            }

            if (cmd.Length < 4)
            {
                MainConsole.Instance.Output("[REGION WEB]: Usage: regionweb currency {0} <request-id> [note]", action);
                return;
            }

            string requestID = cmd[3];
            string note = cmd.Length > 4 ? string.Join(" ", cmd.Skip(4).ToArray()) : string.Empty;
            if (action == "approve")
            {
                ApproveCurrencyPurchase(requestID, note, out string message);
                MainConsole.Instance.Output("[REGION WEB]: " + message);
                return;
            }

            if (action == "deny")
            {
                DenyCurrencyPurchase(requestID, note, out string message);
                MainConsole.Instance.Output("[REGION WEB]: " + message);
                return;
            }

            MainConsole.Instance.Output("[REGION WEB]: Usage: regionweb currency pending|approve|deny");
        }

        private void HandleRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            try
            {
                string path = request.UriPath ?? string.Empty;
                string relative = path.Length > m_basePath.Length ? path.Substring(m_basePath.Length).Trim('/') : string.Empty;

                if (string.IsNullOrEmpty(relative))
                {
                    SendIndex(response);
                    return;
                }

                string[] parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    SendIndex(response);
                    return;
                }

                if (parts.Length >= 2 && parts[0].Equals("media", StringComparison.OrdinalIgnoreCase))
                {
                    SendEstateMedia(string.Join("/", parts.Skip(1).ToArray()), response);
                    return;
                }

                if (parts.Length >= 2 && parts[0].Equals("inventory-carousel", StringComparison.OrdinalIgnoreCase))
                {
                    SendInventoryCarouselAsset(parts[1], response);
                    return;
                }

                if (parts[0].Equals("scripts", StringComparison.OrdinalIgnoreCase))
                {
                    SendScriptReference(parts.Length >= 2 ? parts[1] : string.Empty, response);
                    return;
                }

                if (parts[0].Equals("currency", StringComparison.OrdinalIgnoreCase))
                {
                    SendCurrencyPortal(parts, request, response);
                    return;
                }

                if (parts.Length >= 2 && parts[0].Equals("feature", StringComparison.OrdinalIgnoreCase))
                {
                    SendFeaturePage(parts[1], response);
                    return;
                }

                if (!TryGetScene(parts[0], out Scene scene))
                {
                    SendNotFound(response, "Region page not found.");
                    return;
                }

                if (parts.Length >= 3 && parts[1].Equals("media", StringComparison.OrdinalIgnoreCase))
                {
                    SendMedia(scene, string.Join("/", parts.Skip(2).ToArray()), response);
                    return;
                }

                if (parts.Length >= 3 && parts[1].Equals("post", StringComparison.OrdinalIgnoreCase))
                {
                    SendPost(scene, parts[2], response);
                    return;
                }

                SendRegionPage(scene, response);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[REGION WEB]: Request failed: {0}", e);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.ContentType = "text/plain";
                response.RawBuffer = Encoding.UTF8.GetBytes("Vanilla Sim web request failed.");
            }
        }

        private bool TryGetScene(string slugOrID, out Scene scene)
        {
            UUID regionID;

            lock (m_sync)
            {
                if (!m_regionIDsBySlug.TryGetValue(slugOrID, out regionID))
                {
                    scene = null;
                    return false;
                }

                return m_scenesByID.TryGetValue(regionID, out scene);
            }
        }

        private void SendIndex(IOSHttpResponse response)
        {
            List<Scene> scenes;
            lock (m_sync)
                scenes = new List<Scene>(m_scenesByID.Values);

            EstatePageContent content = LoadEstateContent();
            EstateStats stats = GetEstateStats(scenes);
            string carousel = BuildEstateCarousel(scenes);
            bool hasCarousel = !string.IsNullOrEmpty(carousel);

            StringBuilder html = BeginPage(content.Title);
            html.Append("<header class=\"estate-hero");
            if (!hasCarousel && string.IsNullOrEmpty(content.HeroImage))
                html.Append(" estate-hero-plain");
            html.Append("\"");
            if (!hasCarousel && !string.IsNullOrEmpty(content.HeroImage))
            {
                html.Append(" style=\"background-image:linear-gradient(90deg,rgba(0,0,0,.76),rgba(0,0,0,.30)),url('")
                    .Append(Html(EstateMediaURL(content.HeroImage))).Append("')\"");
            }

            html.Append(">")
                .Append(carousel)
                .Append("<div class=\"wrap\"><p>").Append(Html(content.Tagline)).Append("</p><h1>")
                .Append(Html(content.Title)).Append("</h1>")
                .Append(Paragraphs(content.Description))
                .Append("<div class=\"estate-actions\"><a href=\"#regions\">Explore regions</a><a href=\"#features\">New features</a></div>")
                .Append("</div></header>");

            html.Append("<main><section class=\"wrap estate-stats\"><div>")
                .Append("<strong>").Append(stats.RegionCount.ToString(CultureInfo.InvariantCulture)).Append("</strong><span>Regions online</span></div><div>")
                .Append("<strong>").Append(stats.RootAgents.ToString(CultureInfo.InvariantCulture)).Append("</strong><span>Avatars online</span></div><div>")
                .Append("<strong>").Append(stats.Objects.ToString(CultureInfo.InvariantCulture)).Append("</strong><span>Objects</span></div><div>")
                .Append("<strong>").Append(stats.Prims.ToString(CultureInfo.InvariantCulture)).Append("</strong><span>Prims</span></div><div>")
                .Append("<strong>").Append(stats.MeshParts.ToString(CultureInfo.InvariantCulture)).Append("</strong><span>Mesh parts</span></div></section>");

            html.Append("<section id=\"features\" class=\"wrap feature-section\"><h2>What this estate adds to OpenSim</h2><div class=\"feature-grid\">");
            foreach (FeatureItem feature in content.Features)
            {
                html.Append("<a class=\"feature-card\" href=\"").Append(Html(FeatureURL(feature))).Append("\"><h3>")
                    .Append(Html(feature.Title)).Append("</h3><p>")
                    .Append(Html(feature.Body)).Append("</p><span>Read guide</span></a>");
            }
            html.Append("</div></section>");

            html.Append("<section id=\"regions\" class=\"wrap list\"><h2>Regions</h2><div class=\"region-grid\">");

            foreach (Scene scene in scenes.OrderBy(s => s.RegionInfo.RegionName))
            {
                RegionPageContent regionContent = LoadContent(scene);
                string slug = MakeSlug(scene.RegionInfo.RegionName);
                html.Append("<a class=\"region-card\" href=\"")
                    .Append(Html(m_basePath)).Append("/").Append(Url(slug)).Append("/\">")
                    .Append("<img src=\"").Append(Html(GetHeroURL(scene, regionContent))).Append("\" alt=\"\">")
                    .Append("<strong>").Append(Html(regionContent.Title)).Append("</strong>")
                    .Append("<span>").Append(Html(regionContent.Tagline)).Append("</span>")
                    .Append("</a>");
            }

            html.Append("</div></section></main>");
            html.Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void SendFeaturePage(string slug, IOSHttpResponse response)
        {
            EstatePageContent estate = LoadEstateContent();
            FeatureItem feature = null;

            foreach (FeatureItem item in estate.Features)
            {
                if (MakeSlug(item.Title).Equals(slug, StringComparison.OrdinalIgnoreCase))
                {
                    feature = item;
                    break;
                }
            }

            if (feature == null)
            {
                SendNotFound(response, "Feature page not found.");
                return;
            }

            FeaturePageContent content = LoadFeaturePage(feature);

            StringBuilder html = BeginPage(content.Title + " - " + estate.Title);
            html.Append("<main class=\"wrap feature-page\">");
            AppendPageLinks(html,
                "Estate", m_basePath + "/",
                "Feature menu", m_basePath + "/#features",
                "Script reference", m_basePath + "/scripts",
                "Avatar wallet", m_basePath + "/currency/");
            html.Append("<p class=\"feature-kicker\">Feature guide</p><h1>")
                .Append(Html(content.Title)).Append("</h1><p class=\"lead\">")
                .Append(Html(content.Summary)).Append("</p>");

            html.Append("<section><h2>What it does</h2>")
                .Append(Paragraphs(content.Overview)).Append("</section>");

            AppendFeatureList(html, "How to use it", content.Usage);
            AppendFeatureList(html, "Configuration notes", content.Notes);

            html.Append("</main>").Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void SendScriptReference(string slug, IOSHttpResponse response)
        {
            EstatePageContent estate = LoadEstateContent();
            ScriptFunctionDoc[] docs = GetScriptFunctionDocs();
            ScriptFunctionDoc focus = null;

            if (!string.IsNullOrEmpty(slug))
            {
                foreach (ScriptFunctionDoc doc in docs)
                {
                    if (MakeSlug(doc.Name).Equals(slug, StringComparison.OrdinalIgnoreCase))
                    {
                        focus = doc;
                        break;
                    }
                }

                if (focus == null)
                {
                    SendNotFound(response, "LSL function reference not found.");
                    return;
                }
            }

            StringBuilder html = BeginPage("LSL Compatibility Center - " + estate.Title);
            html.Append("<main class=\"wrap script-reference\">");
            if (focus != null)
            {
                AppendPageLinks(html,
                    "All functions", m_basePath + "/scripts",
                    "Estate", m_basePath + "/",
                    "Feature menu", m_basePath + "/#features",
                    "Avatar wallet", m_basePath + "/currency/");
            }
            else
            {
                AppendPageLinks(html,
                    "Estate", m_basePath + "/",
                    "Feature menu", m_basePath + "/#features",
                    "Avatar wallet", m_basePath + "/currency/");
            }
            html.Append("<p class=\"feature-kicker\">Script compatibility</p><h1>LSL Compatibility Center</h1>")
                .Append("<p class=\"lead\">Expanded Second Life-style LSL functions implemented or corrected in this OpenSim build, with signatures, return values, permissions, compatibility status and exact in-world usage notes.</p>")
                .Append("<p class=\"script-source\">Modeled after the public Second Life LSL function index, but scoped to the functions exposed by this simulator branch and backed by the in-world regression lab in <code>doc/script-engine-examples</code>.</p>");

            AppendScriptCompatibilitySummary(html, docs);

            if (focus != null)
            {
                html.Append("<section class=\"script-focus\">");
                AppendScriptFunctionCard(html, focus, m_basePath, true);
                html.Append("</section></main>").Append(EndPage());
                SendHtml(response, html.ToString());
                return;
            }

            html.Append("<section class=\"script-toc\" id=\"functions\"><h2>Functions</h2><div>");
            foreach (IGrouping<string, ScriptFunctionDoc> group in docs.GroupBy(doc => doc.Category))
            {
                html.Append("<a href=\"#").Append(Html(MakeSlug(group.Key))).Append("\">")
                    .Append(Html(group.Key)).Append(" <span>")
                    .Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append("</span></a>");
            }
            html.Append("</div></section>");

            foreach (IGrouping<string, ScriptFunctionDoc> group in docs.GroupBy(doc => doc.Category))
            {
                html.Append("<section class=\"script-group\" id=\"").Append(Html(MakeSlug(group.Key))).Append("\"><h2>")
                    .Append(Html(group.Key)).Append("</h2>");

                foreach (ScriptFunctionDoc doc in group.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                    AppendScriptFunctionCard(html, doc, m_basePath, false);

                html.Append("</section>");
            }

            html.Append("</main>").Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void SendCurrencyPortal(string[] parts, IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!m_currencyPortalEnabled)
            {
                SendNotFound(response, "Vanilla Sim currency portal is disabled.");
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            bool isPost = !string.IsNullOrEmpty(request.HttpMethod)
                && request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
            string action = isPost ? FormValue(form, "action") :
                (parts.Length >= 2 ? parts[1] : string.Empty);
            bool adminPath = parts.Length >= 2 && parts[1].Equals("admin", StringComparison.OrdinalIgnoreCase);

            CurrencyWebSession session = GetCurrencySession(request);

            if (!isPost && adminPath && parts.Length >= 3)
            {
                if (!IsCurrencyAdminSession(session))
                {
                    SendCurrencyAdminLogin(response, "Login before downloading admin exports.", FormValue(form, "avatar"));
                    return;
                }

                if (parts[2].Equals("requests.csv", StringComparison.OrdinalIgnoreCase))
                {
                    SendCurrencyAdminRequestsCsv(response);
                    return;
                }

                if (parts[2].Equals("balances.csv", StringComparison.OrdinalIgnoreCase))
                {
                    SendCurrencyAdminBalancesCsv(response);
                    return;
                }
            }

            if (!isPost && action.Equals("statement.csv", StringComparison.OrdinalIgnoreCase))
            {
                if (session == null)
                {
                    SendCurrencyLogin(response, "Login before downloading the statement.", FormValue(form, "avatar"));
                    return;
                }

                SendCurrencyStatementCsv(response, session);
                return;
            }

            if (!isPost && action.Equals("paypal-return", StringComparison.OrdinalIgnoreCase))
            {
                if (session == null)
                {
                    SendCurrencyLogin(response, "Login again, then reopen the PayPal return URL to finish the token purchase.", FormValue(form, "avatar"));
                    return;
                }

                HandleCurrencyPayPalReturn(session, form, response);
                return;
            }

            if (!isPost && action.Equals("paypal-cancel", StringComparison.OrdinalIgnoreCase))
            {
                if (session == null)
                {
                    SendCurrencyLogin(response, "PayPal checkout was cancelled. Login again to return to the wallet.", FormValue(form, "avatar"));
                    return;
                }

                HandleCurrencyPayPalCancel(session, form, response);
                return;
            }

            if (action.Equals("logout", StringComparison.OrdinalIgnoreCase))
            {
                if (session != null && !ValidateCurrencyCsrf(session, form, out string csrfMessage))
                {
                    if (adminPath && IsCurrencyAdminSession(session))
                        SendCurrencyAdminDashboard(response, session, csrfMessage, "error");
                    else
                        SendCurrencyDashboard(response, session, csrfMessage, "error");
                    return;
                }

                string sessionToken = ReadCookie(request, CurrencySessionCookie);
                if (!string.IsNullOrEmpty(sessionToken))
                {
                    lock (m_currencyAuthLock)
                        m_currencySessions.Remove(sessionToken);
                }

                ClearCurrencySessionCookie(response);
                if (adminPath)
                    SendCurrencyAdminLogin(response, "You have been logged out.", string.Empty);
                else
                    SendCurrencyLogin(response, "You have been logged out.", string.Empty);
                return;
            }

            if (isPost && action.Equals("request-token", StringComparison.OrdinalIgnoreCase))
            {
                HandleCurrencyTokenRequest(form, response);
                return;
            }

            if (isPost && action.Equals("login", StringComparison.OrdinalIgnoreCase))
            {
                HandleCurrencyLogin(form, response);
                return;
            }

            if (isPost && action.Equals("admin-request-token", StringComparison.OrdinalIgnoreCase))
            {
                HandleCurrencyAdminTokenRequest(form, response);
                return;
            }

            if (isPost && action.Equals("admin-login", StringComparison.OrdinalIgnoreCase))
            {
                HandleCurrencyAdminLogin(form, response);
                return;
            }

            if (action.Equals("admin", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("admin-", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsCurrencyAdminSession(session))
                {
                    SendCurrencyAdminLogin(response, string.Empty, FormValue(form, "avatar"));
                    return;
                }

                if (isPost)
                {
                    string message;
                    string severity;
                    if (ValidateCurrencyCsrf(session, form, out message))
                        HandleCurrencyAdminAction(session, action, form, out message, out severity);
                    else
                        severity = "error";
                    SendCurrencyAdminDashboard(response, session, message, severity);
                    return;
                }

                SendCurrencyAdminDashboard(response, session, string.Empty, string.Empty);
                return;
            }

            if (session == null)
            {
                SendCurrencyLogin(response, string.Empty, FormValue(form, "avatar"));
                return;
            }

            if (isPost && action.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                string message;
                string severity;
                if (ValidateCurrencyCsrf(session, form, out message))
                {
                    if (m_currencyBuyMode.Equals("paypal", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleCurrencyPayPalBuy(session, form, response);
                        return;
                    }

                    HandleCurrencyBuy(session, form, out message, out severity);
                }
                else
                    severity = "error";
                SendCurrencyDashboard(response, session, message, severity);
                return;
            }

            if (isPost && action.Equals("transfer", StringComparison.OrdinalIgnoreCase))
            {
                string message;
                string severity;
                if (ValidateCurrencyCsrf(session, form, out message))
                    HandleCurrencyTransfer(session, form, out message, out severity);
                else
                    severity = "error";
                SendCurrencyDashboard(response, session, message, severity);
                return;
            }

            SendCurrencyDashboard(response, session, string.Empty, string.Empty);
        }

        private void HandleCurrencyTokenRequest(Dictionary<string, string> form, IOSHttpResponse response)
        {
            string avatarName = FormValue(form, "avatar");
            if (!TryResolveAvatar(avatarName, out UUID agentID, out string displayName))
            {
                SendCurrencyLogin(response, "Avatar not found. Use the full avatar name, for example First Last.", avatarName);
                return;
            }

            if (!TryFindOnlineClient(agentID, out IClientAPI client))
            {
                SendCurrencyLogin(response, "Avatar resolved, but it must be online in one of these regions to receive the token inworld.", displayName);
                return;
            }

            string token = GenerateCurrencyChallengeToken();
            DateTime expires = DateTime.UtcNow.AddMinutes(m_currencyChallengeMinutes);
            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                if (m_currencyChallengeCooldownSeconds > 0
                    && m_currencyLastChallengeUTCByAgent.TryGetValue(agentID, out DateTime lastChallengeUTC)
                    && (DateTime.UtcNow - lastChallengeUTC).TotalSeconds < m_currencyChallengeCooldownSeconds)
                {
                    SendCurrencyLogin(response, "Token already sent recently. Wait a few seconds before requesting another one.", displayName);
                    return;
                }

                m_currencyChallenges[token] = new CurrencyLoginChallenge
                {
                    AgentID = agentID,
                    DisplayName = displayName,
                    Token = token,
                    ExpiresUTC = expires
                };
                m_currencyLastChallengeUTCByAgent[agentID] = DateTime.UtcNow;
            }

            string message = "Vanilla Sim wallet login token for " + displayName + ": " + token
                + " (expires in " + m_currencyChallengeMinutes.ToString(CultureInfo.InvariantCulture) + " minutes).";
            try
            {
                client.SendBlueBoxMessage(UUID.Zero, "Vanilla Sim", message);
            }
            catch
            {
                client.SendAgentAlertMessage(message, false);
            }

            SendCurrencyLogin(response, "Token sent inworld to " + displayName + ". Enter it below to open the wallet.", displayName);
        }

        private void HandleCurrencyLogin(Dictionary<string, string> form, IOSHttpResponse response)
        {
            string avatarName = FormValue(form, "avatar");
            string token = FormValue(form, "token").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(token))
            {
                SendCurrencyLogin(response, "Enter the token received inworld.", avatarName);
                return;
            }

            if (!TryResolveAvatar(avatarName, out UUID agentID, out string displayName))
            {
                SendCurrencyLogin(response, "Avatar not found. Request a new token using the exact avatar name.", avatarName);
                return;
            }

            CurrencyLoginChallenge challenge;
            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                if (!m_currencyChallenges.TryGetValue(token, out challenge))
                {
                    SendCurrencyLogin(response, "Invalid or expired token. Request a new one inworld.", displayName);
                    return;
                }

                if (challenge.AgentID != agentID)
                {
                    SendCurrencyLogin(response, "That token belongs to a different avatar.", displayName);
                    return;
                }

                if (challenge.IsAdmin)
                {
                    SendCurrencyLogin(response, "That is an admin token. Use the money admin login page.", displayName);
                    return;
                }

                m_currencyChallenges.Remove(token);
            }

            string sessionToken = GenerateCurrencySessionToken();
            CurrencyWebSession session = new CurrencyWebSession
            {
                AgentID = agentID,
                DisplayName = challenge.DisplayName,
                CsrfToken = GenerateCurrencySessionToken(),
                ExpiresUTC = DateTime.UtcNow.AddHours(m_currencySessionHours)
            };

            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                m_currencySessions[sessionToken] = session;
            }

            SetCurrencySessionCookie(response, sessionToken, session.ExpiresUTC);
            SendCurrencyDashboard(response, session, "Login successful.", "ok");
        }

        private void HandleCurrencyAdminTokenRequest(Dictionary<string, string> form, IOSHttpResponse response)
        {
            string avatarName = FormValue(form, "avatar");
            if (!TryResolveAvatar(avatarName, out UUID agentID, out string displayName))
            {
                SendCurrencyAdminLogin(response, "Avatar not found. Use the full avatar name, for example First Last.", avatarName);
                return;
            }

            if (!IsRegionWebSuperAdmin(agentID))
            {
                SendCurrencyAdminLogin(response, "Only an estate owner of a loaded region can access the Vanilla Sim money admin.", displayName);
                return;
            }

            if (!TryFindOnlineClient(agentID, out IClientAPI client))
            {
                SendCurrencyAdminLogin(response, "Admin avatar resolved, but it must be online in one of these regions to receive the token inworld.", displayName);
                return;
            }

            string token = GenerateCurrencyChallengeToken();
            DateTime expires = DateTime.UtcNow.AddMinutes(m_currencyChallengeMinutes);
            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                if (m_currencyChallengeCooldownSeconds > 0
                    && m_currencyLastChallengeUTCByAgent.TryGetValue(agentID, out DateTime lastChallengeUTC)
                    && (DateTime.UtcNow - lastChallengeUTC).TotalSeconds < m_currencyChallengeCooldownSeconds)
                {
                    SendCurrencyAdminLogin(response, "Admin token already sent recently. Wait a few seconds before requesting another one.", displayName);
                    return;
                }

                m_currencyChallenges[token] = new CurrencyLoginChallenge
                {
                    AgentID = agentID,
                    DisplayName = displayName,
                    Token = token,
                    ExpiresUTC = expires,
                    IsAdmin = true
                };
                m_currencyLastChallengeUTCByAgent[agentID] = DateTime.UtcNow;
            }

            string message = "Vanilla Sim money admin token for " + displayName + ": " + token
                + " (expires in " + m_currencyChallengeMinutes.ToString(CultureInfo.InvariantCulture) + " minutes).";
            try
            {
                client.SendBlueBoxMessage(UUID.Zero, "Vanilla Sim", message);
            }
            catch
            {
                client.SendAgentAlertMessage(message, false);
            }

            SendCurrencyAdminLogin(response, "Admin token sent inworld to " + displayName + ". Enter it below to open money admin.", displayName);
        }

        private void HandleCurrencyAdminLogin(Dictionary<string, string> form, IOSHttpResponse response)
        {
            string avatarName = FormValue(form, "avatar");
            string token = FormValue(form, "token").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(token))
            {
                SendCurrencyAdminLogin(response, "Enter the admin token received inworld.", avatarName);
                return;
            }

            if (!TryResolveAvatar(avatarName, out UUID agentID, out string displayName))
            {
                SendCurrencyAdminLogin(response, "Avatar not found. Request a new admin token using the exact avatar name.", avatarName);
                return;
            }

            if (!IsRegionWebSuperAdmin(agentID))
            {
                SendCurrencyAdminLogin(response, "Only an estate owner of a loaded region can access the Vanilla Sim money admin.", displayName);
                return;
            }

            CurrencyLoginChallenge challenge;
            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                if (!m_currencyChallenges.TryGetValue(token, out challenge))
                {
                    SendCurrencyAdminLogin(response, "Invalid or expired admin token. Request a new one inworld.", displayName);
                    return;
                }

                if (challenge.AgentID != agentID)
                {
                    SendCurrencyAdminLogin(response, "That admin token belongs to a different avatar.", displayName);
                    return;
                }

                if (!challenge.IsAdmin)
                {
                    SendCurrencyAdminLogin(response, "That is a wallet token. Request an admin token from this page.", displayName);
                    return;
                }

                m_currencyChallenges.Remove(token);
            }

            string sessionToken = GenerateCurrencySessionToken();
            CurrencyWebSession session = new CurrencyWebSession
            {
                AgentID = agentID,
                DisplayName = challenge.DisplayName,
                CsrfToken = GenerateCurrencySessionToken(),
                ExpiresUTC = DateTime.UtcNow.AddHours(m_currencySessionHours),
                IsAdmin = true
            };

            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                m_currencySessions[sessionToken] = session;
            }

            SetCurrencySessionCookie(response, sessionToken, session.ExpiresUTC);
            SendCurrencyAdminDashboard(response, session, "Admin login successful.", "ok");
        }

        private void HandleCurrencyAdminAction(CurrencyWebSession session, string action, Dictionary<string, string> form, out string message, out string severity)
        {
            severity = "error";
            if (!IsCurrencyAdminSession(session))
            {
                message = "Admin session expired.";
                return;
            }

            if (action.Equals("admin-approve", StringComparison.OrdinalIgnoreCase))
            {
                if (ApproveCurrencyPurchase(FormValue(form, "request"), FormValue(form, "note"), out message))
                    severity = "ok";
                return;
            }

            if (action.Equals("admin-deny", StringComparison.OrdinalIgnoreCase))
            {
                if (DenyCurrencyPurchase(FormValue(form, "request"), FormValue(form, "note"), out message))
                    severity = "ok";
                return;
            }

            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                message = "Currency module is not active.";
                return;
            }

            if (action.Equals("admin-set-balance", StringComparison.OrdinalIgnoreCase)
                || action.Equals("admin-credit", StringComparison.OrdinalIgnoreCase)
                || action.Equals("admin-debit", StringComparison.OrdinalIgnoreCase))
            {
                string avatar = FormValue(form, "avatar");
                if (!TryResolveAvatar(avatar, out UUID targetID, out string targetName))
                {
                    message = "Avatar not found. Use the full avatar name or UUID.";
                    return;
                }

                int amount;
                if (action.Equals("admin-set-balance", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseWholeAmount(FormValue(form, "amount"), out amount, out message))
                        return;
                }
                else if (!TryParsePositiveAmount(FormValue(form, "amount"), Int32.MaxValue, out amount, out message))
                {
                    return;
                }

                string note = FormValue(form, "note");
                bool result = false;
                string reason;
                if (action.Equals("admin-set-balance", StringComparison.OrdinalIgnoreCase))
                {
                    result = InvokeWebSetBalance(money, targetID, amount, note, out reason);
                    message = result ? "Set " + targetName + " balance to " + amount.ToString(CultureInfo.InvariantCulture) + "." : reason;
                }
                else if (action.Equals("admin-credit", StringComparison.OrdinalIgnoreCase))
                {
                    result = InvokeWebCreditCurrency(money, targetID, amount, note, out reason);
                    message = result ? "Credited " + amount.ToString(CultureInfo.InvariantCulture) + " tokens to " + targetName + "." : reason;
                }
                else
                {
                    result = InvokeWebDebitCurrency(money, targetID, amount, note, out reason);
                    message = result ? "Debited " + amount.ToString(CultureInfo.InvariantCulture) + " tokens from " + targetName + "." : reason;
                }

                if (result)
                    severity = "ok";
                else if (string.IsNullOrWhiteSpace(message))
                    message = "Money admin action failed.";
                return;
            }

            if (action.Equals("admin-transfer", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveAvatar(FormValue(form, "from"), out UUID fromID, out string fromName)
                    || !TryResolveAvatar(FormValue(form, "to"), out UUID toID, out string toName))
                {
                    message = "Both source and destination avatars must resolve to known accounts.";
                    return;
                }

                if (!TryParsePositiveAmount(FormValue(form, "amount"), 0, out int amount, out message))
                    return;

                string note = FormValue(form, "note");
                string description = string.IsNullOrWhiteSpace(note) ? "Vanilla Sim admin transfer" : note;
                if (InvokeWebTransfer(money, fromID, toID, amount, description, out string reason))
                {
                    severity = "ok";
                    message = "Transferred " + amount.ToString(CultureInfo.InvariantCulture) + " tokens from " + fromName + " to " + toName + ".";
                }
                else
                {
                    message = string.IsNullOrWhiteSpace(reason) ? "Admin transfer failed." : reason;
                }
                return;
            }

            message = "Unknown admin action.";
        }

        private void HandleCurrencyBuy(CurrencyWebSession session, Dictionary<string, string> form, out string message, out string severity)
        {
            severity = "error";
            if (!IsCurrencyBuyAvailable())
            {
                message = "Token purchases are disabled on this Vanilla Sim portal.";
                return;
            }

            if (!TryParsePositiveAmount(FormValue(form, "amount"), m_currencyBuyLimit, out int amount, out message))
                return;

            if (m_currencyBuyMode.Equals("paypal", StringComparison.OrdinalIgnoreCase))
            {
                message = "PayPal purchases must start from the wallet checkout button.";
                return;
            }

            if (m_currencyBuyMode.Equals("request", StringComparison.OrdinalIgnoreCase))
            {
                CurrencyPurchaseRequest request = CreateCurrencyPurchaseRequest(session, amount);
                severity = "ok";
                message = "Purchase request " + request.RequestID + " created for " + amount.ToString(CultureInfo.InvariantCulture)
                    + " tokens. Estate staff can approve it from the console.";
                NotifyCurrencyAvatar(session.AgentID, "Vanilla Sim wallet purchase request " + request.RequestID
                    + " created for " + amount.ToString(CultureInfo.InvariantCulture) + " tokens.");
                return;
            }

            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                message = "Currency module is not active.";
                return;
            }

            if (InvokeWebBuyCurrency(money, session.AgentID, amount, out string reason))
            {
                severity = "ok";
                message = "Purchased " + amount.ToString(CultureInfo.InvariantCulture) + " tokens.";
            }
            else
            {
                message = string.IsNullOrWhiteSpace(reason) ? "Token purchase failed." : reason;
            }
        }

        private void HandleCurrencyPayPalBuy(CurrencyWebSession session, Dictionary<string, string> form, IOSHttpResponse response)
        {
            string message;
            if (!m_currencyBuyEnabled || m_currencyBuyMode.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                SendCurrencyDashboard(response, session, "Token purchases are disabled on this Vanilla Sim portal.", "error");
                return;
            }

            if (!TryParsePositiveAmount(FormValue(form, "amount"), m_currencyBuyLimit, out int amount, out message))
            {
                SendCurrencyDashboard(response, session, message, "error");
                return;
            }

            if (!IsPayPalConfigured(out string configReason))
            {
                SendCurrencyDashboard(response, session, configReason, "error");
                return;
            }

            decimal fiatAmount = Decimal.Round(m_payPalPricePerToken * amount, 2, MidpointRounding.AwayFromZero);
            if (fiatAmount <= 0m)
            {
                SendCurrencyDashboard(response, session, "PayPalPricePerToken produces a zero checkout amount.", "error");
                return;
            }

            CurrencyPayPalOrder order = new CurrencyPayPalOrder
            {
                LocalID = GenerateCurrencyPayPalOrderID(),
                AgentID = session.AgentID,
                DisplayName = session.DisplayName,
                TokenAmount = amount,
                FiatAmount = fiatAmount,
                CurrencyCode = m_payPalCurrencyCode,
                Status = "creating",
                CreatedUTC = DateTime.UtcNow,
                UpdatedUTC = DateTime.UtcNow,
                Note = string.Empty
            };

            if (!CreatePayPalOrder(order, out string approvalUrl, out string reason))
            {
                order.Status = "failed";
                order.UpdatedUTC = DateTime.UtcNow;
                order.Note = reason;
                StoreCurrencyPayPalOrder(order);
                SendCurrencyDashboard(response, session, string.IsNullOrWhiteSpace(reason) ? "PayPal order creation failed." : reason, "error");
                return;
            }

            order.Status = "created";
            order.UpdatedUTC = DateTime.UtcNow;
            StoreCurrencyPayPalOrder(order);
            NotifyCurrencyAvatar(session.AgentID, "Vanilla Sim PayPal checkout " + order.LocalID + " created for "
                + amount.ToString(CultureInfo.InvariantCulture) + " tokens.");
            response.Redirect(approvalUrl, HttpStatusCode.Redirect);
        }

        private void HandleCurrencyPayPalReturn(CurrencyWebSession session, Dictionary<string, string> form, IOSHttpResponse response)
        {
            string orderID = FormValue(form, "token");
            string localID = FormValue(form, "local");
            CurrencyPayPalOrder order = FindCurrencyPayPalOrder(orderID, localID);
            if (order == null)
            {
                SendCurrencyDashboard(response, session, "PayPal order not found in Vanilla Sim storage.", "error");
                return;
            }

            if (order.AgentID != session.AgentID)
            {
                SendCurrencyDashboard(response, session, "PayPal order belongs to a different avatar session.", "error");
                return;
            }

            if ((order.Status ?? string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                SendCurrencyDashboard(response, session, "PayPal order " + order.LocalID + " was already completed.", "ok");
                return;
            }

            if (string.IsNullOrWhiteSpace(order.PayPalOrderID))
            {
                SendCurrencyDashboard(response, session, "PayPal order has no remote order id.", "error");
                return;
            }

            bool alreadyCaptured = (order.Status ?? string.Empty).Equals("capture_pending_credit", StringComparison.OrdinalIgnoreCase);
            if (!alreadyCaptured)
            {
                MarkCurrencyPayPalOrder(order.LocalID, "capturing", "Capture requested from PayPal return.");
                if (!CapturePayPalOrder(order.PayPalOrderID, out string captureReason))
                {
                    MarkCurrencyPayPalOrder(order.LocalID, "capture_failed", captureReason);
                    SendCurrencyDashboard(response, session, string.IsNullOrWhiteSpace(captureReason) ? "PayPal capture failed." : captureReason, "error");
                    return;
                }
            }

            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                MarkCurrencyPayPalOrder(order.LocalID, "capture_pending_credit", "PayPal captured, but currency module is not active.");
                SendCurrencyDashboard(response, session, "PayPal payment captured, but the currency module is not active. Admin can credit from the order log.", "error");
                return;
            }

            if (!InvokeWebBuyCurrency(money, order.AgentID, order.TokenAmount, out string creditReason))
            {
                string failure = string.IsNullOrWhiteSpace(creditReason) ? "Currency credit failed after PayPal capture." : creditReason;
                MarkCurrencyPayPalOrder(order.LocalID, "capture_pending_credit", failure);
                SendCurrencyDashboard(response, session, failure, "error");
                return;
            }

            MarkCurrencyPayPalOrder(order.LocalID, "completed", "PayPal captured and tokens credited.");
            NotifyCurrencyAvatar(session.AgentID, "Vanilla Sim PayPal checkout " + order.LocalID + " completed: "
                + order.TokenAmount.ToString(CultureInfo.InvariantCulture) + " tokens credited.");
            SendCurrencyDashboard(response, session, "PayPal payment captured. Credited "
                + order.TokenAmount.ToString(CultureInfo.InvariantCulture) + " tokens.", "ok");
        }

        private void HandleCurrencyPayPalCancel(CurrencyWebSession session, Dictionary<string, string> form, IOSHttpResponse response)
        {
            string orderID = FormValue(form, "token");
            string localID = FormValue(form, "local");
            CurrencyPayPalOrder order = FindCurrencyPayPalOrder(orderID, localID);
            if (order != null && order.AgentID == session.AgentID
                && !(order.Status ?? string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                MarkCurrencyPayPalOrder(order.LocalID, "cancelled", "User cancelled PayPal approval.");
            }

            SendCurrencyDashboard(response, session, "PayPal checkout cancelled.", "ok");
        }

        private void HandleCurrencyTransfer(CurrencyWebSession session, Dictionary<string, string> form, out string message, out string severity)
        {
            severity = "error";
            if (!m_currencyTransferEnabled)
            {
                message = "Wallet transfers are disabled on this Vanilla Sim portal.";
                return;
            }

            if (!TryParsePositiveAmount(FormValue(form, "amount"), 0, out int amount, out message))
                return;

            string recipient = FormValue(form, "recipient");
            if (!TryResolveAvatar(recipient, out UUID recipientID, out string recipientName))
            {
                message = "Recipient avatar not found. Use the full avatar name.";
                return;
            }

            string description = FormValue(form, "description");
            if (description.Length > 160)
                description = description.Substring(0, 160);

            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                message = "Currency module is not active.";
                return;
            }

            if (InvokeWebTransfer(money, session.AgentID, recipientID, amount, description, out string reason))
            {
                severity = "ok";
                message = "Transferred " + amount.ToString(CultureInfo.InvariantCulture) + " tokens to " + recipientName + ".";
            }
            else
            {
                message = string.IsNullOrWhiteSpace(reason) ? "Transfer failed." : reason;
            }
        }

        private void AppendCurrencyGuideCallout(StringBuilder html)
        {
            html.Append("<section class=\"wallet-guide\"><div><span>Currency guide</span><h2>")
                .Append(Html(CurrencyFeatureTitle)).Append("</h2><p>")
                .Append(Html(CurrencyFeatureBody)).Append("</p></div><a href=\"")
                .Append(Html(m_basePath)).Append("/feature/")
                .Append(Url(MakeSlug(CurrencyFeatureTitle)))
                .Append("/\">Read guide</a></section>");
        }

        private void AppendMoneyAdminCallout(StringBuilder html)
        {
            html.Append("<section class=\"wallet-guide wallet-admin-callout\"><div><span>Estate owner tools</span><h2>Money Admin</h2>")
                .Append("<p>Manage pending token requests, avatar balances, exports and local currency operations for the loaded Vanilla Sim estate.</p></div><a href=\"")
                .Append(Html(m_basePath)).Append("/currency/admin\">Open money admin</a></section>");
        }

        private void SendCurrencyLogin(IOSHttpResponse response, string message, string avatarName)
        {
            EstatePageContent estate = LoadEstateContent();
            StringBuilder html = BeginPage("Avatar Wallet - " + estate.Title);
            html.Append("<main class=\"wrap wallet-page\">");
            AppendPageLinks(html,
                "Estate", m_basePath + "/");
            html.Append("<p class=\"feature-kicker\">Reserved area</p><h1>Avatar Wallet</h1>")
                .Append("<p class=\"lead\">Request a one-time token inworld, then use it here to view your balance, statement, token purchases and avatar transfers.</p>");
            AppendCurrencyGuideCallout(html);

            AppendCurrencyMessage(html, message, string.IsNullOrEmpty(message) || message.StartsWith("Token sent", StringComparison.Ordinal) || message.StartsWith("You have", StringComparison.Ordinal) ? "ok" : "error");

            html.Append("<section class=\"wallet-grid\"><article class=\"wallet-card\"><h2>1. Request inworld token</h2>")
                .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/\">")
                .Append("<input type=\"hidden\" name=\"action\" value=\"request-token\">")
                .Append("<label>Avatar name<input name=\"avatar\" value=\"").Append(Html(avatarName)).Append("\" required placeholder=\"First Last\"></label>")
                .Append("<button type=\"submit\">Send token inworld</button></form>")
                .Append("<p class=\"wallet-note\">The avatar must be online in one of the loaded regions so Vanilla Sim can deliver the token through the viewer.</p></article>");

            html.Append("<article class=\"wallet-card\"><h2>2. Login</h2>")
                .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/\">")
                .Append("<input type=\"hidden\" name=\"action\" value=\"login\">")
                .Append("<label>Avatar name<input name=\"avatar\" value=\"").Append(Html(avatarName)).Append("\" required placeholder=\"First Last\"></label>")
                .Append("<label>Token<input name=\"token\" required autocomplete=\"one-time-code\" placeholder=\"8-character token\"></label>")
                .Append("<button type=\"submit\">Open wallet</button></form></article></section></main>")
                .Append(EndPage());

            SendHtml(response, html.ToString());
        }

        private void SendCurrencyAdminLogin(IOSHttpResponse response, string message, string avatarName)
        {
            EstatePageContent estate = LoadEstateContent();
            StringBuilder html = BeginPage("Money Admin - " + estate.Title);
            html.Append("<main class=\"wrap wallet-page\">");
            AppendPageLinks(html,
                "Avatar wallet", m_basePath + "/currency/",
                "Estate", m_basePath + "/");
            html.Append("<p class=\"feature-kicker\">Superadmin area</p><h1>Money Admin</h1>")
                .Append("<p class=\"lead\">Estate owners request a one-time inworld token before managing wallet requests and avatar balances.</p>");

            AppendCurrencyMessage(html, message, string.IsNullOrEmpty(message) || message.StartsWith("Admin token sent", StringComparison.Ordinal) ? "ok" : "error");

            html.Append("<section class=\"wallet-grid\"><article class=\"wallet-card\"><h2>1. Request admin token</h2>")
                .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                .Append("<input type=\"hidden\" name=\"action\" value=\"admin-request-token\">")
                .Append("<label>Estate owner avatar<input name=\"avatar\" value=\"").Append(Html(avatarName)).Append("\" required placeholder=\"First Last\"></label>")
                .Append("<button type=\"submit\">Send admin token inworld</button></form>")
                .Append("<p class=\"wallet-note\">The avatar must be the estate owner of at least one loaded region and must be online.</p></article>");

            html.Append("<article class=\"wallet-card\"><h2>2. Login</h2>")
                .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                .Append("<input type=\"hidden\" name=\"action\" value=\"admin-login\">")
                .Append("<label>Estate owner avatar<input name=\"avatar\" value=\"").Append(Html(avatarName)).Append("\" required placeholder=\"First Last\"></label>")
                .Append("<label>Admin token<input name=\"token\" required autocomplete=\"one-time-code\" placeholder=\"8-character token\"></label>")
                .Append("<button type=\"submit\">Open money admin</button></form></article></section></main>")
                .Append(EndPage());

            SendHtml(response, html.ToString());
        }

        private void SendCurrencyDashboard(IOSHttpResponse response, CurrencyWebSession session, string message, string severity)
        {
            EstatePageContent estate = LoadEstateContent();
            IMoneyModule money = GetCurrencyMoneyModule();
            int balance = 0;
            bool hasBalance = false;
            if (money != null)
            {
                try
                {
                    balance = money.GetBalance(session.AgentID);
                    hasBalance = true;
                }
                catch
                {
                    hasBalance = false;
                }
            }

            StringBuilder html = BeginPage("Avatar Wallet - " + estate.Title);
            html.Append("<main class=\"wrap wallet-page\">");
            AppendPageLinks(html,
                "Estate", m_basePath + "/");
            html.Append("<p class=\"feature-kicker\">Reserved area</p><h1>Avatar Wallet</h1>");
            AppendCurrencyGuideCallout(html);

            AppendCurrencyMessage(html, message, severity);

            html.Append("<section class=\"wallet-summary\"><div><span>Avatar</span><strong>")
                .Append(Html(session.DisplayName)).Append("</strong></div><div><span>Balance</span><strong>")
                .Append(hasBalance ? balance.ToString(CultureInfo.InvariantCulture) : "Unavailable")
                .Append("</strong></div><div><span>Session expires</span><strong>")
                .Append(Html(session.ExpiresUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)))
                .Append("</strong></div></section>");

            if (IsRegionWebSuperAdmin(session.AgentID))
                AppendMoneyAdminCallout(html);

            if (money == null)
            {
                html.Append("<p class=\"wallet-message error\">Currency module is not active. Enable BetaGridLikeMoneyModule in [Economy].</p>");
            }
            else
            {
                html.Append("<section class=\"wallet-grid\"><article class=\"wallet-card\"><h2>Buy tokens</h2>");
                if (IsCurrencyBuyAvailable())
                {
                    html.Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"buy\">")
                        .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                        .Append("<label>Amount<input name=\"amount\" type=\"number\" min=\"1\" max=\"")
                        .Append(m_currencyBuyLimit.ToString(CultureInfo.InvariantCulture)).Append("\" required></label>")
                        .Append("<button type=\"submit\">")
                        .Append(m_currencyBuyMode.Equals("request", StringComparison.OrdinalIgnoreCase) ? "Request tokens"
                            : (m_currencyBuyMode.Equals("paypal", StringComparison.OrdinalIgnoreCase) ? "Pay with PayPal" : "Buy tokens"))
                        .Append("</button></form>");
                    if (m_currencyBuyMode.Equals("request", StringComparison.OrdinalIgnoreCase))
                        html.Append("<p class=\"wallet-note\">This creates a pending purchase request for estate staff approval from the console.</p>");
                    else if (m_currencyBuyMode.Equals("paypal", StringComparison.OrdinalIgnoreCase))
                        html.Append("<p class=\"wallet-note\">Checkout uses PayPal, then credits the local simulator ledger after payment capture. Price: ")
                            .Append(Html(m_payPalPricePerToken.ToString("0.00##", CultureInfo.InvariantCulture))).Append(" ")
                            .Append(Html(m_payPalCurrencyCode)).Append(" per token.</p>");
                    else
                        html.Append("<p class=\"wallet-note\">This credits the local simulator ledger and updates the viewer-visible balance.</p>");
                }
                else
                {
                    if (m_currencyBuyMode.Equals("paypal", StringComparison.OrdinalIgnoreCase) && !IsPayPalConfigured(out string payPalReason))
                        html.Append("<p class=\"wallet-note\">").Append(Html(payPalReason)).Append("</p>");
                    else
                        html.Append("<p class=\"wallet-note\">Token purchases are disabled on this portal.</p>");
                }
                html.Append("</article>");

                html.Append("<article class=\"wallet-card\"><h2>Transfer</h2>");
                if (m_currencyTransferEnabled)
                {
                    html.Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"transfer\">")
                        .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                        .Append("<label>Recipient avatar<input name=\"recipient\" required placeholder=\"First Last\"></label>")
                        .Append("<label>Amount<input name=\"amount\" type=\"number\" min=\"1\" required></label>")
                        .Append("<label>Description<input name=\"description\" maxlength=\"160\" placeholder=\"Optional note\"></label>")
                        .Append("<button type=\"submit\">Transfer tokens</button></form>");
                }
                else
                {
                    html.Append("<p class=\"wallet-note\">Avatar-to-avatar wallet transfers are disabled on this portal.</p>");
                }
                html.Append("</article></section>");

                AppendCurrencyStatement(html, money, session.AgentID);
                AppendCurrencyPurchaseRequests(html, session.AgentID);
            }

            html.Append("<form class=\"wallet-logout\" method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/\">")
                .Append("<input type=\"hidden\" name=\"action\" value=\"logout\">")
                .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                .Append("<button type=\"submit\">Logout</button></form>")
                .Append("</main>").Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void SendCurrencyAdminDashboard(IOSHttpResponse response, CurrencyWebSession session, string message, string severity)
        {
            EstatePageContent estate = LoadEstateContent();
            IMoneyModule money = GetCurrencyMoneyModule();
            StringBuilder html = BeginPage("Money Admin - " + estate.Title);
            html.Append("<main class=\"wrap wallet-page\">");
            AppendPageLinks(html,
                "Avatar wallet", m_basePath + "/currency/",
                "Estate", m_basePath + "/");
            html.Append("<p class=\"feature-kicker\">Superadmin area</p><h1>Money Admin</h1>");

            AppendCurrencyMessage(html, message, severity);

            html.Append("<section class=\"wallet-summary\"><div><span>Admin</span><strong>")
                .Append(Html(session.DisplayName)).Append("</strong></div><div><span>Role</span><strong>Estate owner</strong></div><div><span>Session expires</span><strong>")
                .Append(Html(session.ExpiresUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)))
                .Append("</strong></div></section>");

            if (money == null)
            {
                html.Append("<p class=\"wallet-message error\">Currency module is not active. Enable BetaGridLikeMoneyModule in [Economy].</p>");
            }
            else
            {
                AppendCurrencyAdminRequests(html, session);
                AppendCurrencyAdminPayPalOrders(html);

                html.Append("<section class=\"wallet-grid\"><article class=\"wallet-card\"><h2>Set balance</h2>")
                    .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"admin-set-balance\">")
                    .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                    .Append("<label>Avatar<input name=\"avatar\" required placeholder=\"First Last or UUID\"></label>")
                    .Append("<label>Balance<input name=\"amount\" type=\"number\" min=\"0\" required></label>")
                    .Append("<label>Note<input name=\"note\" maxlength=\"160\" placeholder=\"Optional audit note\"></label>")
                    .Append("<button type=\"submit\">Set balance</button></form></article>");

                html.Append("<article class=\"wallet-card\"><h2>Credit / debit</h2>")
                    .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"admin-credit\">")
                    .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                    .Append("<label>Avatar<input name=\"avatar\" required placeholder=\"First Last or UUID\"></label>")
                    .Append("<label>Amount<input name=\"amount\" type=\"number\" min=\"1\" required></label>")
                    .Append("<label>Note<input name=\"note\" maxlength=\"160\" placeholder=\"Optional audit note\"></label>")
                    .Append("<button type=\"submit\">Credit</button></form>")
                    .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"admin-debit\">")
                    .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                    .Append("<label>Avatar<input name=\"avatar\" required placeholder=\"First Last or UUID\"></label>")
                    .Append("<label>Amount<input name=\"amount\" type=\"number\" min=\"1\" required></label>")
                    .Append("<label>Note<input name=\"note\" maxlength=\"160\" placeholder=\"Optional audit note\"></label>")
                    .Append("<button type=\"submit\">Debit</button></form></article>");

                html.Append("<article class=\"wallet-card\"><h2>Transfer</h2>")
                    .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"admin-transfer\">")
                    .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                    .Append("<label>From<input name=\"from\" required placeholder=\"First Last or UUID\"></label>")
                    .Append("<label>To<input name=\"to\" required placeholder=\"First Last or UUID\"></label>")
                    .Append("<label>Amount<input name=\"amount\" type=\"number\" min=\"1\" required></label>")
                    .Append("<label>Note<input name=\"note\" maxlength=\"160\" placeholder=\"Optional audit note\"></label>")
                    .Append("<button type=\"submit\">Transfer</button></form></article></section>");

                AppendCurrencyAdminBalances(html, money);
            }

            html.Append("<form class=\"wallet-logout\" method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                .Append("<input type=\"hidden\" name=\"action\" value=\"logout\">")
                .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                .Append("<button type=\"submit\">Logout</button></form>")
                .Append("</main>").Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void AppendCurrencyStatement(StringBuilder html, IMoneyModule money, UUID agentID)
        {
            List<Dictionary<string, string>> rows = GetCurrencyStatement(money, agentID);
            html.Append("<section class=\"wallet-card wallet-statement\"><h2>Statement</h2>");
            if (rows.Count == 0)
            {
                html.Append("<p class=\"wallet-note\">No ledger entries yet.</p></section>");
                return;
            }

            html.Append("<p class=\"wallet-note\"><a href=\"").Append(Html(m_basePath))
                .Append("/currency/statement.csv\">Download CSV statement</a></p>");
            html.Append("<div class=\"wallet-table\"><table><thead><tr><th>Date</th><th>Type</th><th>Amount</th><th>Balance</th><th>Description</th></tr></thead><tbody>");
            string agentText = agentID.ToString();
            foreach (Dictionary<string, string> row in rows)
            {
                string amount = RowValue(row, "amount");
                string source = RowValue(row, "source");
                string destination = RowValue(row, "destination");
                bool credit = destination.Equals(agentText, StringComparison.OrdinalIgnoreCase)
                    && !source.Equals(agentText, StringComparison.OrdinalIgnoreCase);
                bool debit = source.Equals(agentText, StringComparison.OrdinalIgnoreCase)
                    && !destination.Equals(agentText, StringComparison.OrdinalIgnoreCase);
                string signedAmount = (credit ? "+" : (debit ? "-" : string.Empty)) + amount;

                html.Append("<tr><td>").Append(Html(FormatUtc(RowValue(row, "utc")))).Append("</td><td>")
                    .Append(Html(RowValue(row, "action"))).Append("</td><td class=\"")
                    .Append(credit ? "credit" : (debit ? "debit" : string.Empty)).Append("\">")
                    .Append(Html(signedAmount)).Append("</td><td>")
                    .Append(Html(RowValue(row, "balance"))).Append("</td><td>")
                    .Append(Html(RowValue(row, "description"))).Append("</td></tr>");
            }

            html.Append("</tbody></table></div></section>");
        }

        private void AppendCurrencyPurchaseRequests(StringBuilder html, UUID agentID)
        {
            List<CurrencyPurchaseRequest> requests;
            lock (m_currencyPurchaseLock)
            {
                requests = m_currencyPurchaseRequests.Values
                    .Where(r => r.AgentID == agentID)
                    .OrderByDescending(r => r.RequestedUTC)
                    .Take(12)
                    .ToList();
            }

            if (requests.Count == 0)
                return;

            html.Append("<section class=\"wallet-card wallet-statement\"><h2>Purchase requests</h2>")
                .Append("<div class=\"wallet-table\"><table><thead><tr><th>Date</th><th>ID</th><th>Amount</th><th>Status</th><th>Note</th></tr></thead><tbody>");

            foreach (CurrencyPurchaseRequest request in requests)
            {
                html.Append("<tr><td>").Append(Html(request.RequestedUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))).Append("</td><td>")
                    .Append(Html(request.RequestID)).Append("</td><td>")
                    .Append(request.Amount.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                    .Append(Html(request.Status)).Append("</td><td>")
                    .Append(Html(request.Note)).Append("</td></tr>");
            }

            html.Append("</tbody></table></div></section>");
        }

        private void AppendCurrencyAdminRequests(StringBuilder html, CurrencyWebSession session)
        {
            List<CurrencyPurchaseRequest> pending;
            lock (m_currencyPurchaseLock)
                pending = m_currencyPurchaseRequests.Values
                    .Where(r => (r.Status ?? string.Empty).Equals("pending", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.RequestedUTC)
                    .ToList();

            html.Append("<section class=\"wallet-card wallet-statement\"><h2>Pending purchase requests</h2>");
            html.Append("<p class=\"wallet-note\"><a href=\"").Append(Html(m_basePath))
                .Append("/currency/admin/requests.csv\">Download requests CSV</a></p>");
            if (pending.Count == 0)
            {
                html.Append("<p class=\"wallet-note\">No pending token purchase requests.</p></section>");
                return;
            }

            html.Append("<div class=\"wallet-table\"><table><thead><tr><th>Date</th><th>ID</th><th>Avatar</th><th>Amount</th><th>Action</th></tr></thead><tbody>");
            foreach (CurrencyPurchaseRequest request in pending)
            {
                html.Append("<tr><td>").Append(Html(request.RequestedUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))).Append("</td><td>")
                    .Append(Html(request.RequestID)).Append("</td><td>")
                    .Append(Html(request.DisplayName)).Append("</td><td>")
                    .Append(request.Amount.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                    .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"admin-approve\">")
                    .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                    .Append("<input type=\"hidden\" name=\"request\" value=\"").Append(Html(request.RequestID)).Append("\">")
                    .Append("<input name=\"note\" maxlength=\"160\" placeholder=\"Note\">")
                    .Append("<button type=\"submit\">Approve</button></form>")
                    .Append("<form method=\"post\" action=\"").Append(Html(m_basePath)).Append("/currency/admin\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"admin-deny\">")
                    .Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Html(session.CsrfToken)).Append("\">")
                    .Append("<input type=\"hidden\" name=\"request\" value=\"").Append(Html(request.RequestID)).Append("\">")
                    .Append("<input name=\"note\" maxlength=\"160\" placeholder=\"Reason\">")
                    .Append("<button type=\"submit\">Deny</button></form></td></tr>");
            }

            html.Append("</tbody></table></div></section>");
        }

        private void AppendCurrencyAdminPayPalOrders(StringBuilder html)
        {
            List<CurrencyPayPalOrder> orders;
            lock (m_currencyPayPalLock)
                orders = m_currencyPayPalOrders.Values
                    .OrderByDescending(r => r.CreatedUTC)
                    .Take(20)
                    .ToList();

            if (orders.Count == 0)
                return;

            html.Append("<section class=\"wallet-card wallet-statement\"><h2>Recent PayPal checkouts</h2>")
                .Append("<div class=\"wallet-table\"><table><thead><tr><th>Date</th><th>ID</th><th>Avatar</th><th>Tokens</th><th>Payment</th><th>Status</th><th>Note</th></tr></thead><tbody>");
            foreach (CurrencyPayPalOrder order in orders)
            {
                html.Append("<tr><td>")
                    .Append(Html(order.CreatedUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))).Append("</td><td>")
                    .Append(Html(order.LocalID)).Append("<br><span>").Append(Html(order.PayPalOrderID)).Append("</span></td><td>")
                    .Append(Html(order.DisplayName)).Append("</td><td>")
                    .Append(order.TokenAmount.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                    .Append(Html(order.FiatAmount.ToString("0.00", CultureInfo.InvariantCulture))).Append(' ')
                    .Append(Html(order.CurrencyCode)).Append("</td><td>")
                    .Append(Html(order.Status)).Append("</td><td>")
                    .Append(Html(order.Note)).Append("</td></tr>");
            }

            html.Append("</tbody></table></div></section>");
        }

        private void AppendCurrencyAdminBalances(StringBuilder html, IMoneyModule money)
        {
            List<Dictionary<string, string>> rows = GetCurrencyBalances(money, 50);
            html.Append("<section class=\"wallet-card wallet-statement\"><h2>Top balances</h2>");
            html.Append("<p class=\"wallet-note\"><a href=\"").Append(Html(m_basePath))
                .Append("/currency/admin/balances.csv\">Download balances CSV</a></p>");
            if (rows.Count == 0)
            {
                html.Append("<p class=\"wallet-note\">No balance rows yet.</p></section>");
                return;
            }

            html.Append("<div class=\"wallet-table\"><table><thead><tr><th>Avatar</th><th>UUID</th><th>Balance</th></tr></thead><tbody>");
            foreach (Dictionary<string, string> row in rows)
            {
                string agentText = RowValue(row, "agent_id");
                string displayName = agentText;
                if (UUID.TryParse(agentText, out UUID agentID))
                {
                    string resolved = LookupAvatarName(agentID);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        displayName = resolved;
                }

                html.Append("<tr><td>").Append(Html(displayName)).Append("</td><td>")
                    .Append(Html(agentText)).Append("</td><td>")
                    .Append(Html(RowValue(row, "balance"))).Append("</td></tr>");
            }

            html.Append("</tbody></table></div></section>");
        }

        private void SendCurrencyStatementCsv(IOSHttpResponse response, CurrencyWebSession session)
        {
            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.ContentType = "text/plain";
                response.RawBuffer = Encoding.UTF8.GetBytes("Currency module is not active.");
                return;
            }

            List<Dictionary<string, string>> rows = GetCurrencyStatement(money, session.AgentID);
            StringBuilder csv = new StringBuilder();
            csv.Append("utc,local_time,action,direction,amount,balance,description,source,destination,success\n");
            string agentText = session.AgentID.ToString();
            foreach (Dictionary<string, string> row in rows)
            {
                string source = RowValue(row, "source");
                string destination = RowValue(row, "destination");
                string direction = GetCurrencyDirection(agentText, source, destination);
                csv.Append(Csv(RowValue(row, "utc"))).Append(',')
                    .Append(Csv(FormatUtc(RowValue(row, "utc")))).Append(',')
                    .Append(Csv(RowValue(row, "action"))).Append(',')
                    .Append(Csv(direction)).Append(',')
                    .Append(Csv(RowValue(row, "amount"))).Append(',')
                    .Append(Csv(RowValue(row, "balance"))).Append(',')
                    .Append(Csv(RowValue(row, "description"))).Append(',')
                    .Append(Csv(source)).Append(',')
                    .Append(Csv(destination)).Append(',')
                    .Append(Csv(RowValue(row, "success"))).Append('\n');
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/csv";
            response.AddHeader("Content-Disposition", "attachment; filename=\"currency-statement-" + MakeSlug(session.DisplayName) + ".csv\"");
            response.RawBuffer = Encoding.UTF8.GetBytes(csv.ToString());
        }

        private void SendCurrencyAdminRequestsCsv(IOSHttpResponse response)
        {
            List<CurrencyPurchaseRequest> requests;
            lock (m_currencyPurchaseLock)
                requests = m_currencyPurchaseRequests.Values
                    .OrderByDescending(r => r.RequestedUTC)
                    .ToList();

            StringBuilder csv = new StringBuilder();
            csv.Append("request_id,requested_utc,local_time,agent_id,display_name,amount,status,updated_utc,operator,note\n");
            foreach (CurrencyPurchaseRequest request in requests)
            {
                csv.Append(Csv(request.RequestID)).Append(',')
                    .Append(Csv(request.RequestedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))).Append(',')
                    .Append(Csv(request.RequestedUTC.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))).Append(',')
                    .Append(Csv(request.AgentID.ToString())).Append(',')
                    .Append(Csv(request.DisplayName)).Append(',')
                    .Append(request.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Csv(request.Status)).Append(',')
                    .Append(Csv(request.UpdatedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))).Append(',')
                    .Append(Csv(request.OperatorName)).Append(',')
                    .Append(Csv(request.Note)).Append('\n');
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/csv";
            response.AddHeader("Content-Disposition", "attachment; filename=\"currency-admin-requests.csv\"");
            response.RawBuffer = Encoding.UTF8.GetBytes(csv.ToString());
        }

        private void SendCurrencyAdminBalancesCsv(IOSHttpResponse response)
        {
            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.ContentType = "text/plain";
                response.RawBuffer = Encoding.UTF8.GetBytes("Currency module is not active.");
                return;
            }

            List<Dictionary<string, string>> rows = GetCurrencyBalances(money, 10000);
            StringBuilder csv = new StringBuilder();
            csv.Append("agent_id,display_name,balance\n");
            foreach (Dictionary<string, string> row in rows)
            {
                string agentText = RowValue(row, "agent_id");
                string displayName = agentText;
                if (UUID.TryParse(agentText, out UUID agentID))
                {
                    string resolved = LookupAvatarName(agentID);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        displayName = resolved;
                }

                csv.Append(Csv(agentText)).Append(',')
                    .Append(Csv(displayName)).Append(',')
                    .Append(Csv(RowValue(row, "balance"))).Append('\n');
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/csv";
            response.AddHeader("Content-Disposition", "attachment; filename=\"currency-admin-balances.csv\"");
            response.RawBuffer = Encoding.UTF8.GetBytes(csv.ToString());
        }

        private void AppendCurrencyMessage(StringBuilder html, string message, string severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string css = string.IsNullOrWhiteSpace(severity) ? "ok" : severity;
            html.Append("<p class=\"wallet-message ").Append(Html(css)).Append("\">")
                .Append(Html(message)).Append("</p>");
        }

        private static bool ValidateCurrencyCsrf(CurrencyWebSession session, Dictionary<string, string> form, out string message)
        {
            message = string.Empty;
            string token = FormValue(form, "csrf");
            if (session == null || string.IsNullOrEmpty(session.CsrfToken)
                || !session.CsrfToken.Equals(token, StringComparison.Ordinal))
            {
                message = "Security token expired. Reload the wallet page and try again.";
                return false;
            }

            return true;
        }

        private IMoneyModule GetCurrencyMoneyModule()
        {
            foreach (Scene scene in GetSceneSnapshot())
            {
                IMoneyModule money = scene.RequestModuleInterface<IMoneyModule>();
                if (money != null)
                    return money;
            }

            return null;
        }

        private bool InvokeWebBuyCurrency(IMoneyModule money, UUID agentID, int amount, out string reason)
        {
            reason = string.Empty;
            MethodInfo method = money.GetType().GetMethod("WebBuyCurrency", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                reason = "Currency module does not expose Vanilla Sim purchases.";
                return false;
            }

            object[] args = new object[] { agentID, amount, reason };
            try
            {
                bool result = method.Invoke(money, args) is bool b && b;
                reason = args[2] as string ?? string.Empty;
                return result;
            }
            catch (Exception e)
            {
                reason = e.InnerException != null ? e.InnerException.Message : e.Message;
                return false;
            }
        }

        private bool InvokeWebTransfer(IMoneyModule money, UUID fromUser, UUID toUser, int amount, string description, out string reason)
        {
            reason = string.Empty;
            MethodInfo method = money.GetType().GetMethod("WebTransfer", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                method = money.GetType().GetMethod("WebTransferCurrency", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                reason = "Currency module does not expose Vanilla Sim transfers.";
                return false;
            }

            object[] args = new object[] { fromUser, toUser, amount, description, reason };
            try
            {
                bool result = method.Invoke(money, args) is bool b && b;
                reason = args[4] as string ?? string.Empty;
                return result;
            }
            catch (Exception e)
            {
                reason = e.InnerException != null ? e.InnerException.Message : e.Message;
                return false;
            }
        }

        private bool InvokeWebSetBalance(IMoneyModule money, UUID agentID, int amount, string description, out string reason)
        {
            return InvokeMoneyAdminMethod(money, "WebSetBalance", agentID, amount, description, out reason);
        }

        private bool InvokeWebCreditCurrency(IMoneyModule money, UUID agentID, int amount, string description, out string reason)
        {
            return InvokeMoneyAdminMethod(money, "WebCreditCurrency", agentID, amount, description, out reason);
        }

        private bool InvokeWebDebitCurrency(IMoneyModule money, UUID agentID, int amount, string description, out string reason)
        {
            return InvokeMoneyAdminMethod(money, "WebDebitCurrency", agentID, amount, description, out reason);
        }

        private bool InvokeMoneyAdminMethod(IMoneyModule money, string methodName, UUID agentID, int amount, string description, out string reason)
        {
            reason = string.Empty;
            MethodInfo method = money.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                reason = "Currency module does not expose " + methodName + ".";
                return false;
            }

            object[] args = new object[] { agentID, amount, description ?? string.Empty, reason };
            try
            {
                bool result = method.Invoke(money, args) is bool b && b;
                reason = args[3] as string ?? string.Empty;
                return result;
            }
            catch (Exception e)
            {
                reason = e.InnerException != null ? e.InnerException.Message : e.Message;
                return false;
            }
        }

        private CurrencyPurchaseRequest CreateCurrencyPurchaseRequest(CurrencyWebSession session, int amount)
        {
            CurrencyPurchaseRequest request = new CurrencyPurchaseRequest
            {
                RequestID = GenerateCurrencyPurchaseRequestID(),
                RequestedUTC = DateTime.UtcNow,
                AgentID = session.AgentID,
                DisplayName = session.DisplayName,
                Amount = amount,
                Status = "pending",
                UpdatedUTC = DateTime.UtcNow,
                OperatorName = string.Empty,
                Note = string.Empty
            };

            lock (m_currencyPurchaseLock)
            {
                m_currencyPurchaseRequests[request.RequestID] = request;
                SaveCurrencyPurchaseRequestsLocked();
            }

            return request;
        }

        private bool ApproveCurrencyPurchase(string requestID, string note, out string message)
        {
            message = string.Empty;
            UUID agentID;
            string displayName;
            int amount;

            lock (m_currencyPurchaseLock)
            {
                if (!m_currencyPurchaseRequests.TryGetValue(requestID ?? string.Empty, out CurrencyPurchaseRequest request))
                {
                    message = "Currency purchase request not found: " + requestID;
                    return false;
                }

                if (!(request.Status ?? string.Empty).Equals("pending", StringComparison.OrdinalIgnoreCase))
                {
                    message = "Currency purchase request " + request.RequestID + " is already " + request.Status + ".";
                    return false;
                }

                request.Status = "processing";
                request.UpdatedUTC = DateTime.UtcNow;
                request.OperatorName = "console";
                request.Note = note ?? string.Empty;
                agentID = request.AgentID;
                displayName = request.DisplayName;
                amount = request.Amount;
                SaveCurrencyPurchaseRequestsLocked();
            }

            IMoneyModule money = GetCurrencyMoneyModule();
            if (money == null)
            {
                MarkCurrencyPurchasePending(requestID, "Currency module is not active.");
                message = "Currency module is not active. Request left pending.";
                return false;
            }

            if (!InvokeWebBuyCurrency(money, agentID, amount, out string reason))
            {
                string failure = string.IsNullOrWhiteSpace(reason) ? "Token purchase failed." : reason;
                MarkCurrencyPurchasePending(requestID, failure);
                message = failure + " Request left pending.";
                return false;
            }

            lock (m_currencyPurchaseLock)
            {
                if (m_currencyPurchaseRequests.TryGetValue(requestID ?? string.Empty, out CurrencyPurchaseRequest request))
                {
                    request.Status = "approved";
                    request.UpdatedUTC = DateTime.UtcNow;
                    request.OperatorName = "console";
                    request.Note = note ?? string.Empty;
                    SaveCurrencyPurchaseRequestsLocked();
                }
            }

            NotifyCurrencyAvatar(agentID, "Vanilla Sim wallet purchase " + requestID + " approved: "
                + amount.ToString(CultureInfo.InvariantCulture) + " tokens credited.");
            message = "Approved " + requestID + " for " + displayName + " and credited "
                + amount.ToString(CultureInfo.InvariantCulture) + " tokens.";
            return true;
        }

        private bool DenyCurrencyPurchase(string requestID, string note, out string message)
        {
            UUID agentID;
            string storedRequestID;
            string displayName;
            string storedNote;

            lock (m_currencyPurchaseLock)
            {
                if (!m_currencyPurchaseRequests.TryGetValue(requestID ?? string.Empty, out CurrencyPurchaseRequest request))
                {
                    message = "Currency purchase request not found: " + requestID;
                    return false;
                }

                string status = request.Status ?? string.Empty;
                if (!status.Equals("pending", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("processing", StringComparison.OrdinalIgnoreCase))
                {
                    message = "Currency purchase request " + request.RequestID + " is already " + request.Status + ".";
                    return false;
                }

                request.Status = "denied";
                request.UpdatedUTC = DateTime.UtcNow;
                request.OperatorName = "console";
                request.Note = note ?? string.Empty;
                SaveCurrencyPurchaseRequestsLocked();

                agentID = request.AgentID;
                storedRequestID = request.RequestID;
                displayName = request.DisplayName;
                storedNote = request.Note;
            }

            NotifyCurrencyAvatar(agentID, "Vanilla Sim wallet purchase " + storedRequestID + " denied."
                + (string.IsNullOrWhiteSpace(storedNote) ? string.Empty : " " + storedNote));
            message = "Denied " + storedRequestID + " for " + displayName + ".";
            return true;
        }

        private void MarkCurrencyPurchasePending(string requestID, string note)
        {
            lock (m_currencyPurchaseLock)
            {
                if (m_currencyPurchaseRequests.TryGetValue(requestID ?? string.Empty, out CurrencyPurchaseRequest request))
                {
                    request.Status = "pending";
                    request.UpdatedUTC = DateTime.UtcNow;
                    request.OperatorName = "console";
                    request.Note = note ?? string.Empty;
                    SaveCurrencyPurchaseRequestsLocked();
                }
            }
        }

        private List<Dictionary<string, string>> GetCurrencyStatement(IMoneyModule money, UUID agentID)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            MethodInfo method = money.GetType().GetMethod("GetCurrencyStatement", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                return rows;

            try
            {
                object result = method.Invoke(money, new object[] { agentID, m_currencyStatementLimit });
                if (result is IEnumerable<Dictionary<string, string>> enumerable)
                    rows.AddRange(enumerable);
            }
            catch
            {
            }

            return rows;
        }

        private List<Dictionary<string, string>> GetCurrencyBalances(IMoneyModule money, int limit)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            MethodInfo method = money.GetType().GetMethod("GetCurrencyBalances", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                return rows;

            try
            {
                object result = method.Invoke(money, new object[] { limit });
                if (result is IEnumerable<Dictionary<string, string>> enumerable)
                    rows.AddRange(enumerable);
            }
            catch
            {
            }

            return rows;
        }

        private bool IsPayPalConfigured(out string reason)
        {
            reason = string.Empty;
            if (!m_payPalEnabled)
            {
                reason = "PayPal checkout is disabled. Set PayPalEnabled = true in [RegionWeb].";
                return false;
            }

            if (string.IsNullOrWhiteSpace(m_payPalClientID) || string.IsNullOrWhiteSpace(m_payPalClientSecret))
            {
                reason = "PayPal checkout needs PayPalClientID and PayPalClientSecret in [RegionWeb].";
                return false;
            }

            if (!IsAbsoluteWebUrl(GetCurrencyPublicBaseUrl()))
            {
                reason = "PayPal checkout needs PayPalReturnBaseUrl set to the public Vanilla Sim URL, for example https://example.com/regionweb.";
                return false;
            }

            if (m_payPalPricePerToken <= 0m)
            {
                reason = "PayPalPricePerToken must be greater than zero.";
                return false;
            }

            return true;
        }

        private bool CreatePayPalOrder(CurrencyPayPalOrder order, out string approvalUrl, out string reason)
        {
            approvalUrl = string.Empty;
            reason = string.Empty;

            if (!GetPayPalAccessToken(out string accessToken, out reason))
                return false;

            string publicBase = GetCurrencyPublicBaseUrl().TrimEnd('/');
            string returnUrl = publicBase + "/currency/paypal-return?local=" + Url(order.LocalID);
            string cancelUrl = publicBase + "/currency/paypal-cancel?local=" + Url(order.LocalID);
            string amount = order.FiatAmount.ToString("0.00", CultureInfo.InvariantCulture);
            string description = "Vanilla Sim wallet tokens for " + order.DisplayName;
            string customID = order.AgentID + ":" + order.TokenAmount.ToString(CultureInfo.InvariantCulture) + ":" + order.LocalID;

            string body =
                "{"
                + "\"intent\":\"CAPTURE\","
                + "\"purchase_units\":[{"
                + "\"reference_id\":\"" + Json(order.LocalID) + "\","
                + "\"description\":\"" + Json(description) + "\","
                + "\"custom_id\":\"" + Json(customID) + "\","
                + "\"amount\":{\"currency_code\":\"" + Json(order.CurrencyCode) + "\",\"value\":\"" + Json(amount) + "\"}"
                + "}],"
                + "\"application_context\":{"
                + "\"brand_name\":\"Vanilla Sim\","
                + "\"landing_page\":\"LOGIN\","
                + "\"user_action\":\"PAY_NOW\","
                + "\"return_url\":\"" + Json(returnUrl) + "\","
                + "\"cancel_url\":\"" + Json(cancelUrl) + "\""
                + "}"
                + "}";

            if (!PayPalPost("/v2/checkout/orders", body, "application/json", accessToken, out string responseText, out reason))
                return false;

            if (!TryParseJsonMap(responseText, out OSDMap map, out reason))
                return false;

            if (!map.TryGetValue("id", out OSD idOSD) || string.IsNullOrWhiteSpace(idOSD.AsString()))
            {
                reason = "PayPal order response did not include an order id.";
                return false;
            }

            order.PayPalOrderID = idOSD.AsString();
            approvalUrl = ExtractPayPalApprovalUrl(map);
            if (string.IsNullOrWhiteSpace(approvalUrl))
            {
                reason = "PayPal order response did not include an approval URL.";
                return false;
            }

            return true;
        }

        private bool CapturePayPalOrder(string paypalOrderID, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(paypalOrderID))
            {
                reason = "PayPal order id is empty.";
                return false;
            }

            if (!GetPayPalAccessToken(out string accessToken, out reason))
                return false;

            string path = "/v2/checkout/orders/" + Url(paypalOrderID) + "/capture";
            if (!PayPalPost(path, "{}", "application/json", accessToken, out string responseText, out reason))
                return false;

            if (!TryParseJsonMap(responseText, out OSDMap map, out reason))
                return false;

            if (map.TryGetValue("status", out OSD statusOSD)
                && statusOSD.AsString().Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                return true;

            reason = "PayPal capture did not return COMPLETED status.";
            return false;
        }

        private bool GetPayPalAccessToken(out string accessToken, out string reason)
        {
            accessToken = string.Empty;
            reason = string.Empty;
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(m_payPalClientID + ":" + m_payPalClientSecret));
            if (!PayPalPost("/v1/oauth2/token", "grant_type=client_credentials", "application/x-www-form-urlencoded", "Basic " + credentials, out string responseText, out reason))
                return false;

            if (!TryParseJsonMap(responseText, out OSDMap map, out reason))
                return false;

            if (!map.TryGetValue("access_token", out OSD tokenOSD) || string.IsNullOrWhiteSpace(tokenOSD.AsString()))
            {
                reason = "PayPal OAuth response did not include an access token.";
                return false;
            }

            accessToken = tokenOSD.AsString();
            return true;
        }

        private bool PayPalPost(string path, string body, string contentType, string authorization, out string responseText, out string reason)
        {
            responseText = string.Empty;
            reason = string.Empty;

            try
            {
                string url = GetPayPalBaseUrl().TrimEnd('/') + path;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = contentType;
                request.Accept = "application/json";
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                if (!string.IsNullOrWhiteSpace(authorization))
                {
                    if (authorization.StartsWith("Basic ", StringComparison.Ordinal) || authorization.StartsWith("Bearer ", StringComparison.Ordinal))
                        request.Headers.Add("Authorization", authorization);
                    else
                        request.Headers.Add("Authorization", "Bearer " + authorization);
                }

                byte[] payload = Encoding.UTF8.GetBytes(body ?? string.Empty);
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    responseText = ReadHttpResponse(response);
                    int code = (int)response.StatusCode;
                    if (code >= 200 && code < 300)
                        return true;

                    reason = "PayPal returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
            }
            catch (WebException e)
            {
                if (e.Response is HttpWebResponse response)
                {
                    responseText = ReadHttpResponse(response);
                    reason = "PayPal returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                        + (string.IsNullOrWhiteSpace(responseText) ? string.Empty : ": " + TrimForLog(responseText, 240));
                    return false;
                }

                reason = "PayPal request failed: " + e.Message;
                return false;
            }
            catch (Exception e)
            {
                reason = "PayPal request failed: " + e.Message;
                return false;
            }
        }

        private static string ReadHttpResponse(HttpWebResponse response)
        {
            if (response == null)
                return string.Empty;

            using (Stream stream = response.GetResponseStream())
            {
                if (stream == null)
                    return string.Empty;

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private static bool TryParseJsonMap(string json, out OSDMap map, out string reason)
        {
            map = null;
            reason = string.Empty;

            try
            {
                map = OSDParser.DeserializeJson(json ?? string.Empty) as OSDMap;
            }
            catch (Exception e)
            {
                reason = "Could not parse PayPal JSON response: " + e.Message;
                return false;
            }

            if (map == null)
            {
                reason = "PayPal JSON response was not an object.";
                return false;
            }

            return true;
        }

        private static string ExtractPayPalApprovalUrl(OSDMap map)
        {
            if (map == null || !map.TryGetValue("links", out OSD linksOSD) || !(linksOSD is OSDArray links))
                return string.Empty;

            foreach (OSD item in links)
            {
                if (!(item is OSDMap link))
                    continue;

                string rel = link.TryGetValue("rel", out OSD relOSD) ? relOSD.AsString() : string.Empty;
                if (!rel.Equals("approve", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (link.TryGetValue("href", out OSD hrefOSD))
                    return hrefOSD.AsString();
            }

            return string.Empty;
        }

        private string GetPayPalBaseUrl()
        {
            return m_payPalEnvironment.Equals("live", StringComparison.OrdinalIgnoreCase)
                ? "https://api-m.paypal.com"
                : "https://api-m.sandbox.paypal.com";
        }

        private string GetCurrencyPublicBaseUrl()
        {
            string configured = (m_payPalReturnBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (IsAbsoluteWebUrl(configured))
                return configured;

            foreach (Scene scene in GetSceneSnapshot())
            {
                string serverURI = (scene.RegionInfo.ServerURI ?? string.Empty).Trim().TrimEnd('/');
                if (IsAbsoluteWebUrl(serverURI))
                    return serverURI + m_basePath;
            }

            return string.Empty;
        }

        private static bool IsAbsoluteWebUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
                && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
        }

        private bool TryParsePositiveAmount(string text, int maxAmount, out int amount, out string reason)
        {
            amount = 0;
            reason = string.Empty;
            if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) || amount <= 0)
            {
                reason = "Amount must be a positive whole number.";
                return false;
            }

            if (maxAmount > 0 && amount > maxAmount)
            {
                reason = "Amount cannot exceed " + maxAmount.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            return true;
        }

        private bool TryParseWholeAmount(string text, out int amount, out string reason)
        {
            amount = 0;
            reason = string.Empty;
            if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) || amount < 0)
            {
                reason = "Amount must be zero or a positive whole number.";
                return false;
            }

            return true;
        }

        private bool IsCurrencyAdminSession(CurrencyWebSession session)
        {
            return session != null && session.IsAdmin && IsRegionWebSuperAdmin(session.AgentID);
        }

        private bool IsRegionWebSuperAdmin(UUID agentID)
        {
            if (agentID == UUID.Zero)
                return false;

            foreach (Scene scene in GetSceneSnapshot())
            {
                EstateSettings estate = scene.RegionInfo.EstateSettings;
                if (estate != null && estate.EstateOwner == agentID)
                    return true;
            }

            return false;
        }

        private CurrencyWebSession GetCurrencySession(IOSHttpRequest request)
        {
            string token = ReadCookie(request, CurrencySessionCookie);
            if (string.IsNullOrEmpty(token))
                return null;

            lock (m_currencyAuthLock)
            {
                CleanupCurrencyAuthLocked();
                if (m_currencySessions.TryGetValue(token, out CurrencyWebSession session))
                    return session;
            }

            return null;
        }

        private void CleanupCurrencyAuthLocked()
        {
            DateTime now = DateTime.UtcNow;
            List<string> expiredChallenges = new List<string>();
            foreach (KeyValuePair<string, CurrencyLoginChallenge> entry in m_currencyChallenges)
            {
                if (entry.Value.ExpiresUTC <= now)
                    expiredChallenges.Add(entry.Key);
            }
            foreach (string token in expiredChallenges)
                m_currencyChallenges.Remove(token);

            List<string> expiredSessions = new List<string>();
            foreach (KeyValuePair<string, CurrencyWebSession> entry in m_currencySessions)
            {
                if (entry.Value.ExpiresUTC <= now)
                    expiredSessions.Add(entry.Key);
            }
            foreach (string token in expiredSessions)
                m_currencySessions.Remove(token);

            if (m_currencyChallengeCooldownSeconds > 0)
            {
                double keepSeconds = Math.Max(3600, m_currencyChallengeCooldownSeconds * 4);
                List<UUID> oldRequests = new List<UUID>();
                foreach (KeyValuePair<UUID, DateTime> entry in m_currencyLastChallengeUTCByAgent)
                {
                    if ((now - entry.Value).TotalSeconds > keepSeconds)
                        oldRequests.Add(entry.Key);
                }
                foreach (UUID agentID in oldRequests)
                    m_currencyLastChallengeUTCByAgent.Remove(agentID);
            }
        }

        private void SetCurrencySessionCookie(IOSHttpResponse response, string token, DateTime expiresUTC)
        {
            response.AddHeader("Set-Cookie", CurrencySessionCookie + "=" + token
                + "; Path=" + m_basePath + "; Expires=" + expiresUTC.ToString("R", CultureInfo.InvariantCulture)
                + "; HttpOnly; SameSite=Lax");
        }

        private void ClearCurrencySessionCookie(IOSHttpResponse response)
        {
            response.AddHeader("Set-Cookie", CurrencySessionCookie
                + "=; Path=" + m_basePath + "; Expires=Thu, 01 Jan 1970 00:00:00 GMT; HttpOnly; SameSite=Lax");
        }

        private static string ReadCookie(IOSHttpRequest request, string name)
        {
            string cookies = request.Headers["cookie"] ?? request.Headers["Cookie"];
            if (string.IsNullOrEmpty(cookies))
                return string.Empty;

            string[] parts = cookies.Split(';');
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                int equals = part.IndexOf('=');
                if (equals <= 0)
                    continue;
                if (part.Substring(0, equals).Trim().Equals(name, StringComparison.Ordinal))
                    return part.Substring(equals + 1).Trim();
            }

            return string.Empty;
        }

        private Dictionary<string, string> ReadForm(IOSHttpRequest request)
        {
            Dictionary<string, string> form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.QueryAsDictionary != null)
            {
                foreach (KeyValuePair<string, string> entry in request.QueryAsDictionary)
                    form[entry.Key] = entry.Value ?? string.Empty;
            }

            if (request.HasEntityBody && request.InputStream != null)
            {
                Encoding encoding = request.ContentEncoding ?? Encoding.UTF8;
                using (StreamReader reader = new StreamReader(request.InputStream, encoding))
                {
                    string body = reader.ReadToEnd();
                    if (!string.IsNullOrEmpty(body))
                    {
                        Dictionary<string, object> parsed = ServerUtils.ParseQueryString(body);
                        foreach (KeyValuePair<string, object> entry in parsed)
                            form[entry.Key] = entry.Value == null ? string.Empty : entry.Value.ToString();
                    }
                }
            }

            return form;
        }

        private static string FormValue(Dictionary<string, string> form, string name)
        {
            if (form != null && form.TryGetValue(name, out string value))
                return value == null ? string.Empty : value.Trim();
            return string.Empty;
        }

        private bool TryResolveAvatar(string value, out UUID agentID, out string displayName)
        {
            agentID = UUID.Zero;
            displayName = string.Empty;
            string name = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                return false;

            if (UUID.TryParse(name, out agentID))
            {
                displayName = LookupAvatarName(agentID);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = agentID.ToString();
                return true;
            }

            if (TryFindOnlineClientByName(name, out IClientAPI client))
            {
                agentID = client.AgentId;
                displayName = client.Name;
                return true;
            }

            if (!SplitAvatarName(name, out string firstName, out string lastName))
                return false;

            foreach (Scene scene in GetSceneSnapshot())
            {
                IUserAccountService accounts = scene.UserAccountService;
                if (accounts == null)
                    continue;

                UserAccount account = accounts.GetUserAccount(scene.RegionInfo.ScopeID, firstName, lastName)
                    ?? accounts.GetUserAccount(UUID.Zero, firstName, lastName);
                if (account == null)
                    continue;

                agentID = account.PrincipalID;
                displayName = account.Name;
                return true;
            }

            return false;
        }

        private string LookupAvatarName(UUID agentID)
        {
            if (TryFindOnlineClient(agentID, out IClientAPI client))
                return client.Name;

            foreach (Scene scene in GetSceneSnapshot())
            {
                IUserAccountService accounts = scene.UserAccountService;
                if (accounts == null)
                    continue;

                UserAccount account = accounts.GetUserAccount(scene.RegionInfo.ScopeID, agentID)
                    ?? accounts.GetUserAccount(UUID.Zero, agentID);
                if (account != null)
                    return account.Name;
            }

            return string.Empty;
        }

        private bool TryFindOnlineClient(UUID agentID, out IClientAPI client)
        {
            client = null;
            foreach (Scene scene in GetSceneSnapshot())
            {
                if (scene.TryGetScenePresence(agentID, out ScenePresence presence)
                    && TryGetRootClient(presence, out client))
                    return true;
            }

            return false;
        }

        private bool TryFindOnlineClientByName(string name, out IClientAPI client)
        {
            client = null;
            if (!SplitAvatarName(name, out string firstName, out string lastName))
                return false;

            foreach (Scene scene in GetSceneSnapshot())
            {
                ScenePresence presence = scene.GetScenePresence(firstName, lastName);
                if (TryGetRootClient(presence, out client))
                    return true;
            }

            return false;
        }

        private static bool TryGetRootClient(ScenePresence presence, out IClientAPI client)
        {
            client = null;
            if (presence == null || presence.IsDeleted || presence.IsChildAgent || presence.ControllingClient == null)
                return false;

            client = presence.ControllingClient;
            return client.IsActive;
        }

        private List<Scene> GetSceneSnapshot()
        {
            lock (m_sync)
                return new List<Scene>(m_scenesByID.Values);
        }

        private static bool SplitAvatarName(string name, out string firstName, out string lastName)
        {
            firstName = string.Empty;
            lastName = string.Empty;
            string[] parts = (name ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            firstName = parts[0];
            if (parts.Length == 1)
                lastName = "Resident";
            else
                lastName = string.Join(" ", parts.Skip(1).ToArray());

            return true;
        }

        private static string GenerateCurrencyChallengeToken()
        {
            return UUID.Random().ToString().Replace("-", string.Empty).Substring(0, 8).ToUpperInvariant();
        }

        private static string GenerateCurrencySessionToken()
        {
            return UUID.Random().ToString().Replace("-", string.Empty) + UUID.Random().ToString().Replace("-", string.Empty);
        }

        private static string GenerateCurrencyPurchaseRequestID()
        {
            return "RW" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                + "-" + UUID.Random().ToString().Replace("-", string.Empty).Substring(0, 6).ToUpperInvariant();
        }

        private static string GenerateCurrencyPayPalOrderID()
        {
            return "PP" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                + "-" + UUID.Random().ToString().Replace("-", string.Empty).Substring(0, 6).ToUpperInvariant();
        }

        private static string NormalizeCurrencyBuyMode(string value)
        {
            string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "request" || mode == "approval" || mode == "approve")
                return "request";
            if (mode == "paypal" || mode == "pay-pal" || mode == "checkout")
                return "paypal";
            if (mode == "disabled" || mode == "disable" || mode == "off" || mode == "false")
                return "disabled";
            return "grant";
        }

        private static string NormalizePayPalEnvironment(string value)
        {
            string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            return mode == "live" || mode == "production" ? "live" : "sandbox";
        }

        private static string NormalizePayPalCurrency(string value)
        {
            string currency = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (currency.Length != 3 || currency.Any(c => c < 'A' || c > 'Z'))
                return "EUR";
            return currency;
        }

        private static decimal ParsePositiveDecimal(string value, decimal fallback)
        {
            if (decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
                && parsed > 0m)
                return parsed;

            if (decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
                && parsed > 0m)
                return parsed;

            return fallback;
        }

        private bool IsCurrencyBuyAvailable()
        {
            if (!m_currencyBuyEnabled || m_currencyBuyMode.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                return false;
            if (m_currencyBuyMode.Equals("paypal", StringComparison.OrdinalIgnoreCase))
                return IsPayPalConfigured(out _);
            return true;
        }

        private void NotifyCurrencyAvatar(UUID agentID, string message)
        {
            if (agentID == UUID.Zero || string.IsNullOrWhiteSpace(message))
                return;

            if (!TryFindOnlineClient(agentID, out IClientAPI client))
                return;

            try
            {
                client.SendBlueBoxMessage(UUID.Zero, "Vanilla Sim", message);
            }
            catch
            {
                try
                {
                    client.SendAgentAlertMessage(message, false);
                }
                catch
                {
                }
            }
        }

        private static string RowValue(Dictionary<string, string> row, string key)
        {
            if (row != null && row.TryGetValue(key, out string value))
                return value ?? string.Empty;
            return string.Empty;
        }

        private static string GetCurrencyDirection(string agentText, string source, string destination)
        {
            if (destination.Equals(agentText, StringComparison.OrdinalIgnoreCase)
                && !source.Equals(agentText, StringComparison.OrdinalIgnoreCase))
                return "credit";
            if (source.Equals(agentText, StringComparison.OrdinalIgnoreCase)
                && !destination.Equals(agentText, StringComparison.OrdinalIgnoreCase))
                return "debit";
            return "internal";
        }

        private static string FormatUtc(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                return parsed.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
            return value;
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            string escaped = value.Replace("\"", "\"\"");
            return mustQuote ? "\"" + escaped + "\"" : escaped;
        }

        private static string Json(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            StringBuilder escaped = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        escaped.Append("\\\\");
                        break;
                    case '"':
                        escaped.Append("\\\"");
                        break;
                    case '\r':
                        escaped.Append("\\r");
                        break;
                    case '\n':
                        escaped.Append("\\n");
                        break;
                    case '\t':
                        escaped.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            escaped.Append(c);
                        break;
                }
            }

            return escaped.ToString();
        }

        private static string TrimForLog(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value ?? string.Empty;
            return value.Substring(0, Math.Max(0, maxLength)) + "...";
        }

        private void LoadCurrencyPurchaseRequests()
        {
            lock (m_currencyPurchaseLock)
            {
                m_currencyPurchaseRequests.Clear();

                if (string.IsNullOrWhiteSpace(m_absoluteCurrencyPurchaseStoragePath)
                    || !File.Exists(m_absoluteCurrencyPurchaseStoragePath))
                    return;

                try
                {
                    foreach (string line in File.ReadAllLines(m_absoluteCurrencyPurchaseStoragePath))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                            continue;

                        string[] parts = line.Split('\t');
                        if (parts.Length < 9 || !UUID.TryParse(parts[2], out UUID agentID))
                            continue;

                        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                            continue;

                        DateTime requestedUTC;
                        DateTime updatedUTC;
                        if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out requestedUTC))
                            requestedUTC = DateTime.UtcNow;
                        if (!DateTime.TryParse(parts[6], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out updatedUTC))
                            updatedUTC = requestedUTC;

                        CurrencyPurchaseRequest request = new CurrencyPurchaseRequest
                        {
                            RequestID = parts[0],
                            RequestedUTC = requestedUTC.ToUniversalTime(),
                            AgentID = agentID,
                            DisplayName = parts[3],
                            Amount = amount,
                            Status = string.IsNullOrWhiteSpace(parts[5]) ? "pending" : parts[5],
                            UpdatedUTC = updatedUTC.ToUniversalTime(),
                            OperatorName = parts[7],
                            Note = parts[8]
                        };

                        if (!string.IsNullOrWhiteSpace(request.RequestID))
                            m_currencyPurchaseRequests[request.RequestID] = request;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[REGION WEB]: Could not load currency purchase requests from {0}: {1}", m_absoluteCurrencyPurchaseStoragePath, e.Message);
                }
            }
        }

        private void SaveCurrencyPurchaseRequestsLocked()
        {
            if (string.IsNullOrWhiteSpace(m_absoluteCurrencyPurchaseStoragePath))
                return;

            try
            {
                string directory = Path.GetDirectoryName(m_absoluteCurrencyPurchaseStoragePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                StringBuilder rows = new StringBuilder();
                rows.Append("# request_id\trequested_utc\tagent_id\tdisplay_name\tamount\tstatus\tupdated_utc\toperator\tnote\n");
                foreach (CurrencyPurchaseRequest request in m_currencyPurchaseRequests.Values.OrderBy(r => r.RequestedUTC))
                {
                    rows.Append(Tsv(request.RequestID)).Append('\t')
                        .Append(request.RequestedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(request.AgentID).Append('\t')
                        .Append(Tsv(request.DisplayName)).Append('\t')
                        .Append(request.Amount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(Tsv(request.Status)).Append('\t')
                        .Append(request.UpdatedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(Tsv(request.OperatorName)).Append('\t')
                        .Append(Tsv(request.Note)).Append('\n');
                }

                File.WriteAllText(m_absoluteCurrencyPurchaseStoragePath, rows.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[REGION WEB]: Could not save currency purchase requests to {0}: {1}", m_absoluteCurrencyPurchaseStoragePath, e.Message);
            }
        }

        private void LoadCurrencyPayPalOrders()
        {
            lock (m_currencyPayPalLock)
            {
                m_currencyPayPalOrders.Clear();

                if (string.IsNullOrWhiteSpace(m_absolutePayPalOrderStoragePath)
                    || !File.Exists(m_absolutePayPalOrderStoragePath))
                    return;

                try
                {
                    foreach (string line in File.ReadAllLines(m_absolutePayPalOrderStoragePath))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                            continue;

                        string[] parts = line.Split('\t');
                        if (parts.Length < 11 || !UUID.TryParse(parts[2], out UUID agentID))
                            continue;

                        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tokenAmount))
                            continue;

                        if (!decimal.TryParse(parts[5], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal fiatAmount))
                            fiatAmount = 0m;

                        DateTime createdUTC;
                        DateTime updatedUTC;
                        if (!DateTime.TryParse(parts[8], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out createdUTC))
                            createdUTC = DateTime.UtcNow;
                        if (!DateTime.TryParse(parts[9], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out updatedUTC))
                            updatedUTC = createdUTC;

                        CurrencyPayPalOrder order = new CurrencyPayPalOrder
                        {
                            LocalID = parts[0],
                            PayPalOrderID = parts[1],
                            AgentID = agentID,
                            DisplayName = parts[3],
                            TokenAmount = tokenAmount,
                            FiatAmount = fiatAmount,
                            CurrencyCode = string.IsNullOrWhiteSpace(parts[6]) ? m_payPalCurrencyCode : parts[6],
                            Status = string.IsNullOrWhiteSpace(parts[7]) ? "created" : parts[7],
                            CreatedUTC = createdUTC.ToUniversalTime(),
                            UpdatedUTC = updatedUTC.ToUniversalTime(),
                            Note = parts[10]
                        };

                        if (!string.IsNullOrWhiteSpace(order.LocalID))
                            m_currencyPayPalOrders[order.LocalID] = order;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[REGION WEB]: Could not load PayPal currency orders from {0}: {1}", m_absolutePayPalOrderStoragePath, e.Message);
                }
            }
        }

        private void StoreCurrencyPayPalOrder(CurrencyPayPalOrder order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.LocalID))
                return;

            lock (m_currencyPayPalLock)
            {
                m_currencyPayPalOrders[order.LocalID] = order;
                SaveCurrencyPayPalOrdersLocked();
            }
        }

        private CurrencyPayPalOrder FindCurrencyPayPalOrder(string paypalOrderID, string localID)
        {
            lock (m_currencyPayPalLock)
            {
                if (!string.IsNullOrWhiteSpace(localID)
                    && m_currencyPayPalOrders.TryGetValue(localID, out CurrencyPayPalOrder byLocal))
                    return byLocal;

                if (!string.IsNullOrWhiteSpace(paypalOrderID))
                {
                    foreach (CurrencyPayPalOrder order in m_currencyPayPalOrders.Values)
                    {
                        if ((order.PayPalOrderID ?? string.Empty).Equals(paypalOrderID, StringComparison.OrdinalIgnoreCase))
                            return order;
                    }
                }
            }

            return null;
        }

        private void MarkCurrencyPayPalOrder(string localID, string status, string note)
        {
            if (string.IsNullOrWhiteSpace(localID))
                return;

            lock (m_currencyPayPalLock)
            {
                if (!m_currencyPayPalOrders.TryGetValue(localID, out CurrencyPayPalOrder order))
                    return;

                order.Status = status ?? order.Status;
                order.Note = note ?? string.Empty;
                order.UpdatedUTC = DateTime.UtcNow;
                SaveCurrencyPayPalOrdersLocked();
            }
        }

        private void SaveCurrencyPayPalOrdersLocked()
        {
            if (string.IsNullOrWhiteSpace(m_absolutePayPalOrderStoragePath))
                return;

            try
            {
                string directory = Path.GetDirectoryName(m_absolutePayPalOrderStoragePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                StringBuilder rows = new StringBuilder();
                rows.Append("# local_id\tpaypal_order_id\tagent_id\tdisplay_name\ttokens\tfiat_amount\tcurrency\tstatus\tcreated_utc\tupdated_utc\tnote\n");
                foreach (CurrencyPayPalOrder order in m_currencyPayPalOrders.Values.OrderBy(r => r.CreatedUTC))
                {
                    rows.Append(Tsv(order.LocalID)).Append('\t')
                        .Append(Tsv(order.PayPalOrderID)).Append('\t')
                        .Append(order.AgentID).Append('\t')
                        .Append(Tsv(order.DisplayName)).Append('\t')
                        .Append(order.TokenAmount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(order.FiatAmount.ToString("0.00", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(Tsv(order.CurrencyCode)).Append('\t')
                        .Append(Tsv(order.Status)).Append('\t')
                        .Append(order.CreatedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(order.UpdatedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(Tsv(order.Note)).Append('\n');
                }

                File.WriteAllText(m_absolutePayPalOrderStoragePath, rows.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[REGION WEB]: Could not save PayPal currency orders to {0}: {1}", m_absolutePayPalOrderStoragePath, e.Message);
            }
        }

        private static string Tsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static void AppendScriptCompatibilitySummary(StringBuilder html, ScriptFunctionDoc[] docs)
        {
            Dictionary<string, int> statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ScriptFunctionDoc doc in docs)
            {
                string status = GetScriptFunctionStatus(doc);
                int count;
                statusCounts.TryGetValue(status, out count);
                statusCounts[status] = count + 1;
            }

            html.Append("<section class=\"script-toc\"><h2>Coverage</h2><div>")
                .Append("<a href=\"#functions\">Total documented <span>")
                .Append(docs.Length.ToString(CultureInfo.InvariantCulture))
                .Append("</span></a>");

            foreach (KeyValuePair<string, int> item in statusCounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                html.Append("<a href=\"#functions\">").Append(Html(item.Key)).Append(" <span>")
                    .Append(item.Value.ToString(CultureInfo.InvariantCulture)).Append("</span></a>");
            }

            html.Append("</div><p class=\"script-source\">Use <code>31_lsl_compatibility_lab_controller.lsl</code> and <code>doc/script-engine-regression/manifest.json</code> as the repeatable regression checklist after each simulator build.</p></section>");
        }

        private static void AppendScriptFunctionCard(StringBuilder html, ScriptFunctionDoc doc, string basePath, bool focused)
        {
            string slug = MakeSlug(doc.Name);
            html.Append("<article class=\"script-card\" id=\"").Append(Html(slug)).Append("\"><div class=\"script-card-head\"><h3>");
            if (focused)
                html.Append(Html(doc.Name));
            else
                html.Append("<a href=\"").Append(Html(basePath)).Append("/scripts/").Append(Html(slug)).Append("\">").Append(Html(doc.Name)).Append("</a>");

            html.Append("</h3><span>").Append(Html(doc.Category)).Append("<br>")
                .Append(Html(GetScriptFunctionStatus(doc))).Append("</span></div>")
                .Append("<p class=\"signature\"><code>").Append(Html(doc.Signature)).Append("</code></p>");

            AppendScriptDetail(html, "Compatibility", GetScriptFunctionCoverage(doc));
            AppendScriptDetail(html, "Returns", doc.ReturnValue);
            AppendScriptDetail(html, "Use", doc.Usage);
            AppendScriptDetail(html, "Permissions", doc.Permissions);
            AppendScriptDetail(html, "Notes", doc.Notes);

            if (!string.IsNullOrWhiteSpace(doc.Example))
            {
                html.Append("<details><summary>Example</summary><pre><code>")
                    .Append(Html(doc.Example)).Append("</code></pre></details>");
            }

            html.Append("</article>");
        }

        private static void AppendScriptDetail(StringBuilder html, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            html.Append("<p class=\"script-detail\"><strong>").Append(Html(label)).Append(":</strong> ")
                .Append(Html(value)).Append("</p>");
        }

        private static string GetScriptFunctionStatus(ScriptFunctionDoc doc)
        {
            if (doc.Category == AutoScriptFunctionCategory)
                return "API surface";

            string name = doc.Name ?? string.Empty;
            string text = ((doc.Permissions ?? string.Empty) + " " + (doc.Notes ?? string.Empty) + " " + (doc.Usage ?? string.Empty)).ToLowerInvariant();

            if (name == "llOpenFloater")
                return "Protocol stub";
            if (name == "llSetSculptAnim")
                return "Viewer-visible fallback";
            if (name == "llScriptProfiler")
                return "Runtime telemetry";
            if (text.Contains("linden's proprietary navmesh"))
                return "OpenSim-local backend";
            if (text.Contains("future"))
                return "Forward-compatible storage";
            if (text.Contains("trusted experience") || text.Contains("experience-lite"))
                return "Experience trust";

            return "Implemented";
        }

        private static string GetScriptFunctionCoverage(ScriptFunctionDoc doc)
        {
            string status = GetScriptFunctionStatus(doc);

            if (status == "Protocol stub")
                return "The SL signature is exposed and returns explicit simulator status, but viewer-side Linden floater services are not part of OpenSim.";
            if (status == "Viewer-visible fallback")
                return "The script API stores the requested SL state and mirrors it through viewer-visible compatibility packets available to OpenSim viewers.";
            if (status == "Runtime telemetry")
                return "The runtime records profiler state and measurements for simulator/Vanilla Sim web inspection; a Linden viewer profiler panel is not part of the OpenSim protocol.";
            if (status == "OpenSim-local backend")
                return "The function is implemented on the local terrain/object/avatar backend because Linden's hosted pathfinding service is unavailable to OpenSim.";
            if (status == "Forward-compatible storage")
                return "The function accepts and persists current values plus future/extension payloads so scripts can be ported without losing data.";
            if (status == "Experience trust")
                return "The function follows SL Experience semantics through this build's local Experience-Lite trust and key-value backend.";
            if (status == "API surface")
                return "Auto-discovered from ILSL_Api so Vanilla Sim web shows the full exposed script surface even before a hand-written compatibility note is added.";

            return "Implemented directly in the simulator script API with Second Life-style arguments, return values and event behavior where applicable.";
        }

        private void SendRegionPage(Scene scene, IOSHttpResponse response)
        {
            RegionPageContent content = LoadContent(scene);
            RegionWebStats stats = GetStats(scene);
            List<BlogPost> posts = LoadPosts(scene).Take(m_postsPerPage).ToList();
            string slug = MakeSlug(scene.RegionInfo.RegionName);
            string carousel = BuildRegionCarousel(scene);
            bool hasCarousel = !string.IsNullOrEmpty(carousel);
            string heroURL = GetHeroURL(scene, content);
            string heroMedia = hasCarousel ? carousel : BuildSingleHeroImage(heroURL, content.Title);

            StringBuilder html = BeginPage(content.Title);
            html.Append("<header class=\"hero\">")
                .Append(heroMedia)
                .Append("<div class=\"wrap\">");
            AppendPageLinks(html,
                "Estate", m_basePath + "/",
                "All regions", m_basePath + "/#regions",
                "Avatar wallet", m_basePath + "/currency/",
                "Script reference", m_basePath + "/scripts");
            html.Append("<p>").Append(Html(content.Tagline)).Append("</p>")
                .Append("<h1>").Append(Html(content.Title)).Append("</h1>")
                .Append("<div class=\"meta\">").Append(Html(scene.RegionInfo.RegionSizeX.ToString(CultureInfo.InvariantCulture)))
                .Append(" x ").Append(Html(scene.RegionInfo.RegionSizeY.ToString(CultureInfo.InvariantCulture)))
                .Append(" m &middot; grid ").Append(Html(scene.RegionInfo.RegionLocX.ToString(CultureInfo.InvariantCulture)))
                .Append(", ").Append(Html(scene.RegionInfo.RegionLocY.ToString(CultureInfo.InvariantCulture))).Append("</div></div></header>");

            html.Append("<main class=\"wrap layout\">");
            html.Append("<section id=\"region-photos\" class=\"story\">").Append(Paragraphs(content.Description));

            if (content.Gallery.Count > 0)
            {
                html.Append("<div class=\"gallery\">");
                foreach (GalleryItem item in content.Gallery)
                {
                    html.Append("<figure><img src=\"").Append(Html(MediaURL(slug, item.FileName))).Append("\" alt=\"")
                        .Append(Html(item.Caption)).Append("\"><figcaption>").Append(Html(item.Caption)).Append("</figcaption></figure>");
                }
                html.Append("</div>");
            }

            html.Append("<h2>Blog</h2>");
            if (posts.Count == 0)
            {
                html.Append("<p class=\"empty\">No posts yet. Add text files to <code>")
                    .Append(Html(Path.Combine(GetRegionDirectory(scene), "posts"))).Append("</code>.</p>");
            }
            else
            {
                foreach (BlogPost post in posts)
                    AppendPostSummary(html, slug, post);
            }

            html.Append("</section><aside class=\"panel\">");

            if (m_showMap)
            {
                html.Append("<img class=\"map\" src=\"").Append(Html(GetMapURL(scene))).Append("\" alt=\"")
                    .Append(Html(scene.RegionInfo.RegionName)).Append(" map\">");
            }

            if (m_showStats)
            {
                AppendStats(html, stats);
                AppendEconomy(html, scene);
            }

            if (m_showParcels && stats.Parcels.Count > 0)
                AppendParcels(html, stats);

            html.Append("</aside></main>");
            html.Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void SendPost(Scene scene, string postSlug, IOSHttpResponse response)
        {
            BlogPost post = LoadPosts(scene).FirstOrDefault(p => p.Slug.Equals(postSlug, StringComparison.OrdinalIgnoreCase));
            if (post == null)
            {
                SendNotFound(response, "Blog post not found.");
                return;
            }

            RegionPageContent content = LoadContent(scene);
            string slug = MakeSlug(scene.RegionInfo.RegionName);

            StringBuilder html = BeginPage(post.Title + " - " + content.Title);
            html.Append("<main class=\"wrap post-page\">");
            AppendPageLinks(html,
                content.Title, m_basePath + "/" + slug + "/",
                "All regions", m_basePath + "/#regions",
                "Estate", m_basePath + "/");
            html.Append("<article class=\"post full\">");

            if (!string.IsNullOrEmpty(post.Image))
                html.Append("<img src=\"").Append(Html(MediaURL(slug, post.Image))).Append("\" alt=\"\">");

            html.Append("<time>").Append(Html(FormatDate(post.Date))).Append("</time>")
                .Append("<h1>").Append(Html(post.Title)).Append("</h1>")
                .Append(Paragraphs(post.Body))
                .Append("</article></main>")
                .Append(EndPage());

            SendHtml(response, html.ToString());
        }

        private void SendMedia(Scene scene, string unsafeName, IOSHttpResponse response)
        {
            string fileName = Path.GetFileName(unsafeName);
            if (string.IsNullOrEmpty(fileName))
            {
                SendNotFound(response, "Media not found.");
                return;
            }

            string path = Path.Combine(GetRegionDirectory(scene), "media", fileName);
            if (!File.Exists(path))
            {
                SendNotFound(response, "Media not found.");
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = GetContentType(path);
            response.RawBuffer = File.ReadAllBytes(path);
        }

        private void SendEstateMedia(string unsafeName, IOSHttpResponse response)
        {
            string fileName = Path.GetFileName(unsafeName);
            if (string.IsNullOrEmpty(fileName))
            {
                SendNotFound(response, "Media not found.");
                return;
            }

            string path = Path.Combine(m_absoluteContentDirectory, "media", fileName);
            if (!File.Exists(path))
            {
                SendNotFound(response, "Media not found.");
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = GetContentType(path);
            response.RawBuffer = File.ReadAllBytes(path);
        }

        private void SendInventoryCarouselAsset(string unsafeName, IOSHttpResponse response)
        {
            if (!TryParseInventoryCarouselAssetID(unsafeName, out UUID assetID))
            {
                SendNotFound(response, "Inventory carousel image not found.");
                return;
            }

            if (!TryFindInventoryCarouselItem(assetID, out Scene scene, out InventoryItemBase item))
            {
                SendNotFound(response, "Inventory carousel image not found.");
                return;
            }

            if (TryGetCachedInventoryCarouselAsset(assetID, out byte[] cachedData, out string cachedContentType))
            {
                SendInventoryCarouselImageResponse(response, cachedData, cachedContentType);
                return;
            }

            AssetBase asset = null;
            try
            {
                asset = scene.AssetService.Get(item.AssetID.ToString());
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[REGION WEB]: Could not fetch inventory carousel asset {0}: {1}", assetID, e.Message);
            }

            if (asset == null || asset.Data == null || asset.Data.Length == 0)
            {
                SendNotFound(response, "Inventory carousel image not found.");
                return;
            }

            if (!TryEncodeInventoryCarouselAsset(scene, asset, out byte[] data, out string contentType))
            {
                SendNotFound(response, "Inventory carousel image could not be decoded.");
                return;
            }

            SetCachedInventoryCarouselAsset(assetID, data, contentType);
            SendInventoryCarouselImageResponse(response, data, contentType);
        }

        private EstatePageContent LoadEstateContent()
        {
            EstatePageContent content = new EstatePageContent();
            content.Title = m_defaultEstateTitle;
            content.Tagline = "Vanilla Sim hypergrid estate";
            content.Description = "Vanilla Sim runs a tuned OpenSim build with richer maps, better region presentation, weather, visitor tools and simulator polish.";
            content.HeroImage = string.Empty;
            AddDefaultFeatures(content.Features);

            string file = Path.Combine(m_absoluteContentDirectory, "estate.ini");
            if (!File.Exists(file))
                return content;

            IniConfigSource source;
            try
            {
                source = new IniConfigSource(file);
            }
            catch
            {
                return content;
            }

            IConfig config = source.Configs["EstateWeb"];
            if (config == null)
                return content;

            content.Title = config.GetString("Title", content.Title).Trim();
            content.Tagline = config.GetString("Tagline", content.Tagline).Trim();
            content.Description = config.GetString("Description", content.Description).Trim();
            content.HeroImage = config.GetString("HeroImage", string.Empty).Trim();
            if (IsLegacyEstateBrand(content.Title))
                content.Title = "Vanilla Sim";
            if (IsLegacyEstateBrand(content.Tagline))
                content.Tagline = "Vanilla Sim hypergrid estate";

            List<FeatureItem> configuredFeatures = ParseFeatures(config.GetString("Features", string.Empty));
            if (configuredFeatures.Count == 0)
                configuredFeatures = ParseNumberedFeatures(config);
            if (configuredFeatures.Count > 0)
            {
                content.Features.Clear();
                content.Features.AddRange(NormalizeFeatures(configuredFeatures));
            }
            EnsureFeature(content.Features, "Wave-following boats",
                "Boats can now move with the sea surface, following wave motion for a more natural marina and sailing experience.");
            EnsureFeature(content.Features, "Smooth region crossings",
                "Avatar and vehicle crossings between neighbouring regions are smoothed to reduce the hard stop, rubber-banding and visual pop of stock OpenSim border transfers.");
            EnsureFeature(content.Features, "Lag-resistant walk animations",
                "Walking animations recover cleanly after lag spikes, so avatars do not remain stuck in broken walk states when the simulator catches up.");
            EnsureFeature(content.Features, "AI-connected text build tools",
                "Estate builders can use text commands connected to AI or uploaded cartography textures to plan, generate and refine terrain or building ideas directly from the simulator workflow.");
            EnsureFeature(content.Features, "Automatic cloud avatar recovery",
                "If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds.");
            EnsureFeature(content.Features, "Group auto invite",
                "Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.");
            EnsureFeature(content.Features, CurrencyFeatureTitle, CurrencyFeatureBody);
            EnsureFeature(content.Features, MultiGridFeatureTitle, MultiGridFeatureBody);
            EnsureFeature(content.Features, "Viewer polish",
                "Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent.");
            EnsureFeature(content.Features, ScriptEngineFeatureTitle, ScriptEngineFeatureBody);

            return content;
        }

        private FeaturePageContent LoadFeaturePage(FeatureItem feature)
        {
            FeaturePageContent content = GetDefaultFeaturePage(feature);
            string file = Path.Combine(m_absoluteContentDirectory, "features", MakeSlug(feature.Title) + ".ini");
            if (!File.Exists(file))
                return content;

            IniConfigSource source;
            try
            {
                source = new IniConfigSource(file);
            }
            catch
            {
                return content;
            }

            IConfig config = source.Configs["Feature"];
            if (config == null)
                return content;

            FeaturePageContent defaults = GetDefaultFeaturePage(feature);

            content.Title = config.GetString("Title", content.Title).Trim();
            content.Summary = config.GetString("Summary", content.Summary).Trim();
            content.Overview = config.GetString("Overview", content.Overview).Trim();

            List<string> usage = ParseFeatureList(config, "Usage");
            if (usage.Count > 0)
                content.Usage = usage;

            List<string> notes = ParseFeatureList(config, "Note");
            if (notes.Count > 0)
                content.Notes = notes;

            MergeFeaturePageDefaults(content, defaults, IsScriptEngineFeature(feature.Title));

            return content;
        }

        private static void MergeFeaturePageDefaults(FeaturePageContent content, FeaturePageContent defaults, bool preferDefaultText)
        {
            if (preferDefaultText)
            {
                content.Title = defaults.Title;
                content.Summary = defaults.Summary;
                content.Overview = defaults.Overview;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(content.Summary))
                    content.Summary = defaults.Summary;
                if (string.IsNullOrWhiteSpace(content.Overview))
                    content.Overview = defaults.Overview;
            }

            if (preferDefaultText)
            {
                AppendMissingFeatureItems(content.Usage, defaults.Usage);
                AppendMissingFeatureItems(content.Notes, defaults.Notes);
            }
        }

        private static void AppendMissingFeatureItems(List<string> target, List<string> defaults)
        {
            foreach (string item in defaults)
            {
                if (!target.Any(existing => existing.Equals(item, StringComparison.OrdinalIgnoreCase)))
                    target.Add(item);
            }
        }

        private static FeaturePageContent GetDefaultFeaturePage(FeatureItem feature)
        {
            string slug = MakeSlug(feature.Title);
            FeaturePageContent content = new FeaturePageContent
            {
                Title = feature.Title,
                Summary = feature.Body,
                Overview = feature.Body
            };

            switch (slug)
            {
                case "high-quality-world-map":
                    content.Overview = "The world map renderer produces a sharper source tile before the viewer ever zooms it. It combines terrain texture sampling, depth-aware water color, aerial tone mapping, mesh and sculpt projection, cleaner alpha handling for water overlays, and cooperative render passes that avoid starving the simulator while heavy object scenes are being drawn.";
                    content.Usage.Add("Keep GenerateMaptiles enabled in [Map] and use MapImageModule for the region map renderer.");
                    content.Usage.Add("Enable texture sampling and mesh/sculpt aware rendering when you want detailed marinas, vehicles, sculpt builds and textured terrain to appear correctly on the map.");
                    content.Usage.Add("Use the console command generate map after changing terrain, water objects, large builds or map settings.");
                    content.Usage.Add("If wave planes or animated water overlays pollute the tile, lower MapWaterObjectVolumeOpacity or keep texture alpha sampling enabled.");
                    content.Notes.Add("Very large opaque builds are still rendered, while transparent water-like overlays are drawn faintly so they do not become grey rectangles.");
                    content.Notes.Add("Background and cooperative rendering make the feature safer on busy regions, but a manual map render can still be expensive on very dense scenes.");
                    break;

                case "regionweb-pages":
                case "regionweb-estate-portal":
                    content.Title = "Vanilla Sim estate portal";
                    content.Summary = "Every estate and region can publish a web page with photos, posts, map tiles, parcels and live simulator statistics.";
                    content.Overview = "Vanilla Sim turns the simulator HTTP endpoint into a simple estate website. The central page lists online regions and estate features; each region gets a profile page, gallery, blog posts, current map tile, parcel summaries and live stats pulled from the simulator.";
                    content.Usage.Add("Open /regionweb/ on the simulator HTTP address to view the estate landing page.");
                    content.Usage.Add("Edit bin/RegionWeb/estate.ini for the central title, tagline, hero image and feature cards.");
                    content.Usage.Add("Edit bin/RegionWeb/<region-slug>/profile.ini for each region page, and add JPEG or PNG files under that region's media folder.");
                    content.Usage.Add("RegionWeb auto-creates inventory folders for owner-managed carousel images: RegionWeb Carousel for the estate landing page, and RegionWeb <Region Name> Carousel for each region page. Drop inworld snapshots or textures into those folders to replace generated map tiles.");
                    content.Usage.Add("Create posts as text files under bin/RegionWeb/<region-slug>/posts/ using the Title, Date, Summary, Image and body format created by the sample file.");
                    content.Notes.Add("The module auto-creates starter folders without overwriting existing content.");
                    break;

                case "weather-and-visitor-polish":
                case "weather-module":
                    content.Title = "Weather module";
                    content.Summary = "Regions can run rain, storm, snow or sunny presets with wind, clouds, lightning, thunder and automatic forecast cycling.";
                    content.Overview = "The weather system adds estate-controlled atmosphere without needing scripted emitters scattered by hand. It can change clouds and wind, spawn particle weather, announce forecasts and cycle between presets after startup.";
                    content.Usage.Add("Weather is enabled by default for the showcase profiles; estate managers use /89 weather rain, /89 weather storm, /89 weather snow, /89 weather sunny, /89 weather clear and /89 weather status.");
                    content.Usage.Add("Configure AutoCycleEnabled, AutoCycleHours and AutoCycleChoices to let the region rotate between storm, rain, snow, sunny and clear.");
                    content.Usage.Add("Tune EmitterGrid, Intensity, wind strengths and lightning delays per region style.");
                    content.Usage.Add("Use forecast warning and entry IM messages when visitors should know current and upcoming conditions.");
                    content.Notes.Add("Large storms create many emitters, so keep intensity and emitter spacing reasonable on busy regions.");
                    break;

                case "wave-following-boats":
                    content.Overview = "Boat motion can now be tied to the sea surface, so vessels sit and move more naturally with waves instead of looking glued to a flat mathematical plane. This is especially visible in marinas, harbors and aerial views where water movement and boats should agree.";
                    content.Usage.Add("Use boats or vehicle scripts that opt into the estate wave-following behavior.");
                    content.Usage.Add("Keep the object close to the water surface so the server can apply the intended vertical motion cleanly.");
                    content.Usage.Add("After updating an old boat, rerezzing it is the quickest way to ensure it starts with fresh motion state.");
                    content.Notes.Add("The feature improves visual motion; it does not require visitors to install a special viewer.");
                    break;

                case "smooth-region-crossings":
                    content.Overview = "Region crossings are softened so avatars and vehicles do not hit the border with the abrupt stop, rubber-banding and visual pop that stock OpenSim can show during transfer. The goal is to make neighbouring regions feel like one larger continuous place.";
                    content.Usage.Add("Keep neighbouring regions online, adjacent and reachable through the normal simulator neighbour connection.");
                    content.Usage.Add("Use consistent simulator builds and compatible physics settings on regions that share a border.");
                    content.Usage.Add("Test crossings with both walking avatars and vehicles after changing region size, physics or network settings.");
                    content.Notes.Add("Crossing quality still depends on network latency and the target region being healthy, but the server now reduces the harsh visual transition.");
                    break;

                case "lag-resistant-walk-animations":
                    content.Overview = "When the simulator lags, avatars can sometimes remain visually stuck in a bad walking state even after movement resumes. This build recovers walk animation state when the simulator catches up, so visitors do not stay trapped in broken locomotion.";
                    content.Usage.Add("No viewer-side action is required; the server handles recovery automatically.");
                    content.Usage.Add("If a region is under heavy load, wait a few seconds after the spike before judging animation state.");
                    content.Usage.Add("Keep custom AO scripts reasonable, because very aggressive scripted animation overrides can still fight normal movement animation.");
                    content.Notes.Add("This does not hide real simulator load; it prevents lag from leaving avatars visually broken after the load spike passes.");
                    break;

                case "ai-connected-text-build-tools":
                    content.Overview = "The text build tool connects in-world commands to AI-assisted building workflows. Builders can describe what they want, iterate on ideas and use text as a faster control surface for terrain, layout or object planning inside the simulator workflow.";
                    content.Usage.Add("Enable the text build module and use its configured in-world command channel.");
                    content.Usage.Add("Speak concise build requests on that channel, then refine the result with follow-up instructions.");
                    content.Usage.Add("To generate real-world shaped terrain, upload a cartography or satellite texture and say a command such as build terrain from texture <uuid> or costruisci Sardegna da texture <uuid>.");
                    content.Usage.Add("Use it for planning, layout, terrain and fast creative iteration, then review the generated changes like any other build work.");
                    content.Notes.Add("AI-assisted building should stay permission-aware: restrict access to trusted builders or estate staff.");
                    content.Notes.Add("Cartography terrain treats cyan/celeste map areas as sea, fits the detected land/water silhouette while preserving source aspect ratio, keeps the coastline mask sharp while smoothing inland terrain, ignores child-region chat events and sets water height to 21m.");
                    break;

                case "automatic-cloud-avatar-recovery":
                    content.Overview = "If an avatar enters the region as a cloud because appearance data or baked textures are incomplete, the server now manages the recovery path and restores the normal appearance within a few seconds. The visitor does not have to relog or manually rebake as often.";
                    content.Usage.Add("Leave the recovery feature enabled on regions where visitor appearance reliability matters.");
                    content.Usage.Add("When a visitor appears as a cloud, wait for the server recovery window before asking them to relog.");
                    content.Usage.Add("Keep asset and inventory services healthy, because the recovery still needs the avatar's saved wearables and textures to be available.");
                    content.Notes.Add("The server avoids saving temporary fallback appearance as the user's real outfit.");
                    break;

                case "group-auto-invite":
                    content.Overview = "Regions can invite arriving root avatars to a configured group using the normal viewer group invitation popup. This replaces fragile scripted invite objects with a server-side region module.";
                    content.Usage.Add("Enable [GroupAutoInvite] and set GroupID or GroupName.");
                    content.Usage.Add("Optionally set InviterID, RoleID, InviteDelaySeconds and a custom InviteMessage.");
                    content.Usage.Add("Keep InviteOncePerSession enabled if visitors should not be spammed after teleports or relogs.");
                    content.Notes.Add("The module sends an invitation; it does not force users to join.");
                    break;

                case "viewer-visible-local-currency":
                case "local-currency-economy":
                case "currency":
                case "money-module":
                    content.Title = CurrencyFeatureTitle;
                    content.Summary = CurrencyFeatureBody;
                    content.Overview = CurrencyFeatureOverview;
                    content.Usage.Add("Enable the economy module with economymodule = BetaGridLikeMoneyModule in [Economy].");
                    content.Usage.Add("Set InitialBalance to the amount a new avatar should receive the first time they appear in the ledger.");
                    content.Usage.Add("Set BalanceStorage to choose where the persistent TSV balance ledger is stored; relative paths live under the OpenSim bin folder.");
                    content.Usage.Add("Set TransactionLog and AuditEnabled to control the TSV transaction audit trail.");
                    content.Usage.Add("Keep AllowNegativeBalances = false for normal viewer currency behavior unless you deliberately want overdraft-style testing.");
                    content.Usage.Add("Set the LoginService Currency value to the viewer-facing currency name you want users to see beside their balance.");
                    content.Usage.Add("Use console commands such as money show, money balance, money set, money give, money take, money transfer, money export and money import for estate administration.");
                    content.Usage.Add("Open /regionweb/currency/ for the reserved avatar wallet area: users request an inworld token, log in on Vanilla Sim, view balance/statement, buy tokens and transfer to another avatar.");
                    content.Usage.Add("Estate owners can open /regionweb/currency/admin, request an inworld admin token and manage pending token requests plus avatar balances.");
                    content.Usage.Add("Use CurrencyBuyEnabled, CurrencyTransferEnabled, CurrencyChallengeCooldownSeconds, CurrencyStatementLimit and CurrencyBuyLimit in [RegionWeb] to tune the wallet.");
                    content.Usage.Add("Set CurrencyBuyMode = request if purchases should become pending wallet requests instead of immediate credits.");
                    content.Usage.Add("Set CurrencyBuyMode = paypal plus PayPalEnabled, PayPalClientID, PayPalClientSecret, PayPalCurrencyCode, PayPalPricePerToken and PayPalReturnBaseUrl to route wallet token purchases through PayPal Checkout before crediting the local ledger.");
                    content.Usage.Add("From /regionweb/currency/admin, use requests.csv and balances.csv to export pending purchase requests and avatar balances for external audit.");
                    content.Usage.Add("Use regionweb currency pending, regionweb currency approve <request-id> and regionweb currency deny <request-id> to manage pending wallet purchase requests from the simulator console.");
                    content.Usage.Add("Restart the region after changing economy settings, then log in or request the balance in the viewer to receive the latest MoneyBalanceReply.");
                    content.Notes.Add("Balances are local to this simulator/grid configuration and are intended for estate/gameplay currency, not real-money production payment processing.");
                    content.Notes.Add("Scripted llGiveMoney and llTransferLindenDollars still require owner-granted PERMISSION_DEBIT before money leaves the object owner.");
                    content.Notes.Add("Object payments trigger the normal money event path, so in-world vendors and donation jars can react when the viewer pays an object.");
                    content.Notes.Add("Land/object purchases and upload/group charges use the same ledger, so users see the result immediately in the viewer balance.");
                    content.Notes.Add("Vanilla Sim region pages show live economy totals when this local money module is active, and the wallet login uses a one-time inworld token instead of trusting only a typed avatar name.");
                    content.Notes.Add("Wallet buy, transfer and logout forms are protected by a session CSRF token, and the statement can be downloaded as CSV.");
                    content.Notes.Add("Pending wallet purchase requests are stored in CurrencyPurchaseStorage as TSV so they survive simulator restarts.");
                    content.Notes.Add("PayPal checkout orders are stored in PayPalOrderStorage as TSV; tokens are credited only after the PayPal order capture returns completed.");
                    break;

                case "multi-grid-region-attachments":
                case "multi-grid-attachments":
                case "multigrid":
                case "multi-grid":
                    content.Title = MultiGridFeatureTitle;
                    content.Summary = MultiGridFeatureBody;
                    content.Overview = MultiGridFeatureOverview;
                    content.Usage.Add("Enable [MultiGridAttachments] and list attachment names in Grids, for example Grids = \"osgrid,neverworld,zetasim,craft\".");
                    content.Usage.Add("Create one [MultiGridAttachment.<name>] section per target with GridServerURI pointing at the target grid service root, such as http://grid.example.com:8002.");
                    content.Usage.Add("For OSGrid, use GridServerURI = http://grid.osgrid.org and GridPostURI = http://grid.osgrid.org/grid; http://hg.osgrid.org:80 is the Hypergrid login/gatekeeper endpoint, not the region registration endpoint.");
                    content.Usage.Add("Set GridPostURI when the target's registration endpoint is not simply GridServerURI plus /grid; Neverworld uses http://hg.neverworldgrid.com:8003/grid for region registration, while 8002 is its public login/gatekeeper endpoint.");
                    content.Usage.Add("For ZetaWorlds, use GridServerURI = http://robust.zetaworlds.com:8003 and GridPostURI = http://robust.zetaworlds.com:8003/grid; http://hg.zetaworlds.com:80 is the Hypergrid login/gatekeeper endpoint.");
                    content.Usage.Add("For Craft World, use GridServerURI = http://craft-world.org:8003 and GridPostURI = http://craft-world.org:8003/grid with BasicHttpAuthentication; http://craft-world.org:8002 is the Hypergrid login/gatekeeper endpoint.");
                    content.Usage.Add("Set ExternalHostName and ServerURI to your public DNS endpoint, for example vanilla-sim.com and http://vanilla-sim.com:9000.");
                    content.Usage.Add("Leave Regions empty to publish every local region, or list region names/UUIDs to publish only selected regions.");
                    content.Usage.Add("Use Location = x,y when the secondary grid needs a different map coordinate for the published region.");
                    content.Usage.Add("Use RegionNamePrefix, RegionNameSuffix or RegionName when the target grid needs unique names.");
                    content.Usage.Add("Set AuthType = BasicHttpAuthentication plus HttpAuthUsername/HttpAuthPassword when a private friend's grid protects its grid service.");
                    content.Usage.Add("Keep AutoCreateInboundPresence = true so teleports from an attached grid map can create the local presence row before Scene.VerifyUserPresence runs.");
                    content.Usage.Add("Keep Strict = false, ContinueOnFailure = true and a short TimeoutSeconds value for public grids so rejected or slow attachments run in the background and do not stop the simulator.");
                    content.Notes.Add("This is a publication/attachment layer. The simulator still has one primary grid for inventory, assets, accounts and presence.");
                    content.Notes.Add("Public grids may reject registration unless they explicitly allow your region server or provide credentials.");
                    content.Notes.Add("Use a DNS name rather than a raw IP address for Hypergrid-facing endpoints; some grids refuse raw-IP HG addresses.");
                    break;

                case "viewer-polish":
                    content.Overview = "Viewer-facing polish keeps neighbouring regions feeling like one estate. The simulator can send a stable branded version string to viewers so teleports and crossings do not produce noisy different-version warnings when local builds or operating systems differ.";
                    content.Usage.Add("Set SendSimulatorVersionToViewer and ViewerSimulatorVersionOverride in [ClientStack.LindenUDP].");
                    content.Usage.Add("Use the same override string across estate regions that should feel like one coherent grid experience.");
                    content.Notes.Add("This is presentation polish only; keep the actual simulator binaries compatible for crossings and shared services.");
                    break;

                case "second-life-style-script-engine":
                case "experience-lite-script-permissions":
                case "experience-lite-key-value-store":
                    content.Title = ScriptEngineFeatureTitle;
                    content.Summary = ScriptEngineFeatureBody;
                    content.Overview = ScriptEngineFeatureOverview;
                    content.Usage.Add("Enable [ScriptExperiences] only in trusted estate environments.");
                    content.Usage.Add("Add trusted script owner UUIDs to TrustedOwners, or specific root object/prim UUIDs to TrustedObjects.");
                    content.Usage.Add("Keep AutoGrantPermissions limited to the permissions your estate systems actually need.");
                    content.Usage.Add("Use llRequestPermissions normally from scripts; trusted requests are granted automatically when covered by the configured bitmask.");
                    content.Usage.Add("Use llIsExperienceTrusted(), llAgentInExperience(agent), llGetExperienceDetails(NULL_KEY), llGetExperiencePermissions() and llExperienceCanAutoGrant(mask) when scripts need to adapt to trusted or untrusted regions.");
                    content.Usage.Add("Use llRequestExperiencePermissions(agent, name) with experience_permissions(agent) and experience_permissions_denied(agent, reason) for SL-style Experience-Lite scripts.");
                    content.Usage.Add("Use llSitOnLink(agent, link) after experience_permissions(agent) to seat visitors on a specific linked sit target; it returns SL-style SIT_* result codes.");
                    content.Usage.Add("Use llSetLinkSitFlags(link, flags), llGetLinkSitFlags(link), PRIM_SCRIPTED_SIT_ONLY and PRIM_ALLOW_UNSIT to create seats that cannot be taken by a normal viewer sit click but can be controlled by trusted scripts.");
                    content.Usage.Add("Use llCreateKeyValue(key, value), llReadKeyValue(key), llUpdateKeyValue(key, value, checked, originalValue), llDeleteKeyValue(key), llDataSizeKeyValue(), llKeyCountKeyValue() and llKeysKeyValue(first, count).");
                    content.Usage.Add("Use llGetExperienceKeyValueStoreStats() to inspect enabled/trusted state, key counts, byte usage and configured KVP limits.");
                    content.Usage.Add("Handle dataserver(queryid, data). Replies use 1,value for success and 0,errorCode for failure.");
                    content.Usage.Add("Use llGetExperienceErrorMessage(errorCode) to turn failure codes into readable script diagnostics.");
                    content.Usage.Add("Use llLinksetDataWrite(), llLinksetDataRead(), llLinksetDataDelete(), protected variants, pattern search/list helpers and linkset_data(action, name, value) for object-local persistent state.");
                    content.Usage.Add("Use llRezObjectWithParams() with REZ_* and REZ_FLAG_* constants for SL-style parameterized rezzing, and llDerezObject() for scripted cleanup.");
                    content.Usage.Add("Use llLinkPlaySound(), llLinkStopSound(), llLinkAdjustSoundVolume(), llLinkSetSoundRadius() and llLinkSetSoundQueueing() with SOUND_* flags for linked sound control.");
                    content.Usage.Add("Use llGetDayLength(), llGetRegionDayLength(), llGetDayOffset(), llGetRegionDayOffset(), llGetSunDirection(), llGetRegionSunDirection(), llGetMoonDirection(), llGetRegionMoonDirection(), llGetSunRotation(), llGetRegionSunRotation(), llGetMoonRotation() and llGetRegionMoonRotation() for environment-aware scripts.");
                    content.Usage.Add("Use llGetEnvironment(), llReplaceEnvironment(), llReplaceAgentEnvironment(), llSetEnvironment() and llSetAgentEnvironment() for supported EEP day-cycle, parcel, region, agent, sky-parameter and water-parameter workflows.");
                    content.Usage.Add("Use llReturnObjectsByID(), llReturnObjectsByOwner(), OBJECT_RETURN_* and PERMISSION_RETURN_OBJECTS for scripted estate/parcel cleanup.");
                    content.Usage.Add("Use llSetParcelForSale(forSale, options), PARCEL_SALE_* and PERMISSION_PRIVILEGED_LAND_ACCESS for scripted parcel sale workflows when the script owner owns the parcel.");
                    content.Usage.Add("Use llParcelMediaCommandList(), llParcelMediaQuery(), PARCEL_MEDIA_COMMAND_LOOP_SET, media description/type/size and auto-align fields for SL-style parcel media controllers.");
                    content.Usage.Add("Use llGetParcelPrimCount(pos, PARCEL_COUNT_*, sim_wide) for local parcel and same-owner simulator-wide prim counters, including PARCEL_COUNT_TEMP.");
                    content.Usage.Add("Use llGetParcelDetails(pos, [PARCEL_DETAILS_PRIM_CAPACITY, PARCEL_DETAILS_PRIM_USED]) for SL-style same-owner simulator-wide parcel capacity and usage.");
                    content.Usage.Add("Use llGiveMoney() or llTransferLindenDollars() only after owner-granted PERMISSION_DEBIT; group-owned objects and non-avatar targets are rejected.");
                    content.Usage.Add("Use llSetGroundTexture() with TERRAIN_DETAIL_* and TERRAIN_HEIGHT_RANGE_* to update estate terrain textures and blending heights.");
                    content.Usage.Add("Use llSetRenderMaterial(), llSetLinkRenderMaterial(), llSetLinkGLTFOverrides(), llGetRenderMaterial(), llIsLinkGLTFMaterial(), PRIM_RENDER_MATERIAL, PRIM_GLTF_* and OVERRIDE_GLTF_* constants for PBR/material-aware content, including primitive-param render-material set/get, assigned GLTF material asset readback, stored GLTF override readback and OVERRIDE_GLTF_EXTENSION_JSON staging for future extension JSON.");
                    content.Usage.Add("Use PRIM_PHYSICS_MATERIAL through llSetPrimitiveParams(), llGetPrimitiveParams(), llSetLinkPrimitiveParams() and llGetLinkPrimitiveParams() for SL-order gravity, restitution, friction and density workflows.");
                    content.Usage.Add("Use llMatchGroup(agent, group_keys) for same-region active-group checks without scripted llSameGroup relay objects.");
                    content.Usage.Add("Use llSetDamage(), llDamage(), llGetHealth(), PRIM_DAMAGE, PRIM_HEALTH, OBJECT_HEALTH, OBJECT_DAMAGE, OBJECT_DAMAGE_TYPE and DAMAGE_TYPE_* constants for supported health/damage workflows; on_damage scripts can call llAdjustDamage() before the transaction applies health.");
                    content.Usage.Add("Pathfinding scripts can compile against llCreateCharacter(), llNavigateTo(), llGetStaticPath() and related CHARACTER_* APIs; OpenSim now persists character options, routes over a baked terrain navmesh cache with A*, avoids scene-object and optional avatar bounds, honors parcel-stay settings and posts path_update when movement completes where the proprietary SL navmesh service is unavailable.");
                    content.Usage.Add("Use llHMAC(), llComputeHash(), llSignRSA() and llVerifyRSA() for signature checks, web callbacks and secure scripted handshakes.");
                    content.Usage.Add("Use llGetAttachedListFiltered(), ATTACH_ANY_HUD, FILTER_INCLUDE and FILTER_FLAG_HUDS for filtered attachment queries.");
                    content.Usage.Add("Use llDetectedRezzer() from sensor/collision/touch-style detected data when scripts need to identify object provenance.");
                    content.Usage.Add("Use llKey2Name(), llGetUsername(), llGetDisplayName(), llName2Key(), llRequestUsername(), llRequestDisplayName() and llRequestUserKey() for SL-style identity lookup backed by live scene data and cached local user accounts.");
                    content.Usage.Add("Use llGetObjectDetails(id, [OBJECT_SERVER_COST, OBJECT_STREAMING_COST, OBJECT_PHYSICS_COST, OBJECT_PRIM_EQUIVALENCE, OBJECT_RENDER_WEIGHT, OBJECT_HOVER_HEIGHT, OBJECT_SELECT_COUNT]) for SL-style object and avatar diagnostics.");
                    content.Usage.Add("Use llFindNotecardTextSync() for cached synchronous notecard text search.");
                    content.Usage.Add("Use llGiveAgentInventory(), TRANSFER_DEST, TRANSFER_FLAGS, TRANSFER_* result codes, llTransferOwnership(), TRANSFER_FLAG_COPY and TRANSFER_FLAG_TAKE for SL-style direct delivery and ownership workflows where estate trust and simulator support allow them.");
                    content.Usage.Add("Use the bundled script-engine examples, including the EEP sky environment console, PBR GLTF physics primitive-param lab, object details diagnostics console, identity lookup console and parcel prim count auditor, to verify each newly implemented LSL feature in-world.");
                    content.Usage.Add("Use llWorldPosToHUD() for HUDs that need to point at or track in-world positions.");
                    content.Usage.Add("Use llGetStartString() when scripts need SL-style start parameter data after rez.");
                    content.Usage.Add("llSetSculptAnim() stores the requested sculpt animation state and mirrors it through the normal texture-animation packet so viewers can see sculpt texture playback even though OpenSim has no separate sculpt-animation protocol field.");
                    content.Usage.Add("Open the Vanilla Sim scripts page at /regionweb/scripts for the per-function reference with signatures, return values, permissions and usage notes.");
                    content.Notes.Add("The default permission bitmask excludes PERMISSION_DEBIT and ownership changes.");
                    content.Notes.Add("Untrusted scripts keep the normal viewer permission prompt behavior.");
                    content.Notes.Add("The store is scoped per region/owner and persisted under KeyValueStorePath, making it useful for estate tools, games, rides and AI build workflows.");
                    content.Notes.Add("Use KeyValueStoreMaxKeys, KeyValueStoreMaxKeyBytes, KeyValueStoreMaxValueBytes, KeyValueStoreMaxStoreBytes and KeyValueStorePath to tune storage.");
                    content.Notes.Add("New constants documented by the script runtime include XP_ERROR_*, SIT_*, SIT_FLAG_*, LINKSETDATA_*, SOUND_*, REZ_*, REZ_FLAG_*, PARCEL_SALE_*, PARCEL_MEDIA_COMMAND_*, OBJECT_RETURN_*, OBJECT_* detail constants, ENV_*, SKY_*, WATER_*, TERRAIN_*, TRANSFER_*, FILTER_*, DAMAGE_TYPE_*, CHARACTER_*, PU_*, PRIM_SCRIPTED_SIT_ONLY, PRIM_ALLOW_UNSIT, PRIM_SIT_TARGET, PRIM_RENDER_MATERIAL, PRIM_GLTF_*, OVERRIDE_GLTF_* including OVERRIDE_GLTF_EXTENSION_JSON, PRIM_PHYSICS_MATERIAL and CHANGED_RENDER_MATERIAL.");
                    content.Notes.Add("The pathfinding backend is simulator-side A* over a region-local baked terrain cache plus dynamic object/avatar clearance bounds, not Linden Lab's proprietary baked navmesh generator. Combat2 damage adjustment is applied before health through a quiet-window transaction capped by a server-side timeout. Sculpt animation uses viewer-visible texture animation as the compatible transport. Profiler mode and counters are stored on prim dynamic attributes for local tooling/Vanilla Sim web rather than a Linden viewer-only profiler capability.");
                    content.Notes.Add("Existing Vanilla Sim feature files are merged with these built-in defaults at render time, so older auto-generated pages pick up the newer LSL surface without deleting local notes.");
                    break;
            }

            return content;
        }

        private static bool IsLegacyEstateBrand(string value)
        {
            string lower = value.ToLowerInvariant();
            string oldPersonalBrand = "gun" + "thar";
            return (lower.Contains("opensimulator") && lower.Contains("estate"))
                || (lower.Contains("standalone hypergrid") && lower.Contains("estate"))
                || lower.Contains(oldPersonalBrand);
        }

        private RegionPageContent LoadContent(Scene scene)
        {
            RegionPageContent content = new RegionPageContent();
            content.Title = scene.RegionInfo.RegionName;
            content.Tagline = "A Vanilla Sim region";
            content.Description = "Add region photos and a description in this region.s Vanilla Sim content folder.";
            content.HeroImage = string.Empty;

            string file = Path.Combine(GetRegionDirectory(scene), "profile.ini");
            if (!File.Exists(file))
                return content;

            IniConfigSource source;
            try
            {
                source = new IniConfigSource(file);
            }
            catch
            {
                return content;
            }

            IConfig config = source.Configs["RegionWeb"];
            if (config == null)
                return content;

            content.Title = config.GetString("Title", content.Title).Trim();
            content.Tagline = config.GetString("Tagline", content.Tagline).Trim();
            content.Description = config.GetString("Description", content.Description).Trim();
            content.HeroImage = config.GetString("HeroImage", string.Empty).Trim();

            string gallery = config.GetString("Gallery", string.Empty);
            foreach (string entry in gallery.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(new[] { '|' }, 2);
                string media = parts[0].Trim();
                if (string.IsNullOrEmpty(media))
                    continue;

                content.Gallery.Add(new GalleryItem
                {
                    FileName = media,
                    Caption = parts.Length > 1 ? parts[1].Trim() : Path.GetFileNameWithoutExtension(media)
                });
            }

            return content;
        }

        private List<BlogPost> LoadPosts(Scene scene)
        {
            string postsDir = Path.Combine(GetRegionDirectory(scene), "posts");
            if (!Directory.Exists(postsDir))
                return new List<BlogPost>();

            List<BlogPost> posts = new List<BlogPost>();
            foreach (string file in Directory.GetFiles(postsDir, "*.txt"))
            {
                BlogPost post = LoadPost(file);
                if (post != null)
                    posts.Add(post);
            }

            return posts
                .OrderByDescending(p => p.Date)
                .ThenBy(p => p.Title)
                .ToList();
        }

        private BlogPost LoadPost(string file)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                return null;
            }

            BlogPost post = new BlogPost();
            post.Title = Path.GetFileNameWithoutExtension(file);
            post.Slug = MakeSlug(post.Title);
            post.Date = File.GetLastWriteTime(file);
            post.Summary = string.Empty;
            post.Image = string.Empty;

            int bodyStart = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim() == "----")
                {
                    bodyStart = i + 1;
                    break;
                }

                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;

                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();

                if (key.Equals("Title", StringComparison.OrdinalIgnoreCase))
                    post.Title = value;
                else if (key.Equals("Date", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsedDate))
                    post.Date = parsedDate;
                else if (key.Equals("Summary", StringComparison.OrdinalIgnoreCase))
                    post.Summary = value;
                else if (key.Equals("Image", StringComparison.OrdinalIgnoreCase))
                    post.Image = value;
            }

            post.Slug = MakeSlug(Path.GetFileNameWithoutExtension(file));
            post.Body = string.Join("\n", lines.Skip(bodyStart).ToArray()).Trim();
            if (string.IsNullOrEmpty(post.Summary))
                post.Summary = FirstWords(post.Body, 32);

            return post;
        }

        private EstateStats GetEstateStats(List<Scene> scenes)
        {
            EstateStats estateStats = new EstateStats();
            estateStats.RegionCount = scenes.Count;

            foreach (Scene scene in scenes)
            {
                RegionWebStats stats = GetStats(scene);
                estateStats.RootAgents += stats.RootAgents;
                estateStats.ChildAgents += stats.ChildAgents;
                estateStats.NPCs += stats.NPCs;
                estateStats.Objects += stats.Objects;
                estateStats.Prims += stats.Prims;
                estateStats.MeshParts += stats.MeshParts;
                estateStats.SculptParts += stats.SculptParts;
                estateStats.ParcelCount += stats.ParcelCount;
            }

            return estateStats;
        }

        private RegionWebStats GetStats(Scene scene)
        {
            RegionWebStats stats = new RegionWebStats();
            stats.SimFPS = scene.StatsReporter.LastReportedSimFPS;

            foreach (ScenePresence presence in scene.GetScenePresences())
            {
                if (presence.IsChildAgent)
                    stats.ChildAgents++;
                else if (presence.IsNPC)
                    stats.NPCs++;
                else
                    stats.RootAgents++;
            }

            List<SceneObjectGroup> groups = scene.GetSceneObjectGroups();
            stats.Objects = groups.Count;
            foreach (SceneObjectGroup group in groups)
            {
                stats.Prims += group.PrimCount;
                foreach (SceneObjectPart part in group.Parts)
                {
                    if (part.Shape != null && part.Shape.SculptType == (byte)SculptType.Mesh)
                        stats.MeshParts++;
                    else if (part.Shape != null && part.Shape.SculptEntry)
                        stats.SculptParts++;
                }
            }

            if (scene.LandChannel != null)
            {
                List<ILandObject> parcels = scene.LandChannel.AllParcels();
                stats.ParcelCount = parcels.Count;
                foreach (ILandObject parcel in parcels.OrderByDescending(p => p.LandData.Area).Take(8))
                {
                    stats.Parcels.Add(new ParcelSummary
                    {
                        Name = parcel.LandData.Name,
                        Area = parcel.LandData.Area
                    });
                }
            }

            return stats;
        }

        private void EnsureEstateContent()
        {
            string mediaDir = Path.Combine(m_absoluteContentDirectory, "media");
            string featuresDir = Path.Combine(m_absoluteContentDirectory, "features");
            Directory.CreateDirectory(mediaDir);
            Directory.CreateDirectory(featuresDir);
            EnsureDefaultFeaturePages(featuresDir);

            string file = Path.Combine(m_absoluteContentDirectory, "estate.ini");
            if (File.Exists(file))
                return;

            File.WriteAllText(file,
                "[EstateWeb]\n"
                + "Title = \"" + EscapeIni(m_defaultEstateTitle) + "\"\n"
                + "Tagline = \"Polished for creators and visitors\"\n"
                + "Description = \"A public portal for regions, maps, news and technical improvements. This build keeps OpenSim's flexibility while adding a cleaner visitor experience, better cartography, richer presentation pages and smoother simulator startup behavior.\"\n"
                + "HeroImage = \"\"\n"
                + "; Feature entries use title|description.\n"
                + "Feature1 = \"High quality world map|Terrain textures, water depth shading, land detail, aerial tone mapping, mesh/sculpt geometry projection, cleaner water alpha handling, background generation and cooperative rendering make map tiles sharper, more geographic and safer for simulator responsiveness.\"\n"
                + "Feature2 = \"Vanilla Sim estate portal|Every region can have a public web page with photos, blog posts, map tile, parcels and live region statistics.\"\n"
                + "Feature3 = \"Weather module|Regions can run rain, storm, snow or sunny presets, with wind, clouds, lightning, thunder and automatic forecast cycling.\"\n"
                + "Feature4 = \"Wave-following boats|Boats can now move with the sea surface, following wave motion for a more natural marina and sailing experience.\"\n"
                + "Feature5 = \"Smooth region crossings|Avatar and vehicle crossings between neighbouring regions are smoothed to reduce the hard stop, rubber-banding and visual pop of stock OpenSim border transfers.\"\n"
                + "Feature6 = \"Lag-resistant walk animations|Walking animations recover cleanly after lag spikes, so avatars do not remain stuck in broken walk states when the simulator catches up.\"\n"
                + "Feature7 = \"AI-connected text build tools|Estate builders can use text commands connected to AI or uploaded cartography textures to plan, generate and refine terrain or building ideas directly from the simulator workflow.\"\n"
                + "Feature8 = \"Automatic cloud avatar recovery|If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds.\"\n"
                + "Feature9 = \"Group auto invite|Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.\"\n"
                + "Feature10 = \"" + CurrencyFeatureTitle + "|" + CurrencyFeatureBody + "\"\n"
                + "Feature11 = \"Viewer polish|Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent.\"\n"
                + "Feature12 = \"" + ScriptEngineFeatureTitle + "|" + ScriptEngineFeatureBody + "\"\n",
                new UTF8Encoding(false));
        }

        private void EnsureDefaultFeaturePages(string featuresDir)
        {
            List<FeatureItem> features = new List<FeatureItem>();
            AddDefaultFeatures(features);

            foreach (FeatureItem feature in features)
            {
                string file = Path.Combine(featuresDir, MakeSlug(feature.Title) + ".ini");
                if (File.Exists(file))
                    continue;

                FeaturePageContent content = GetDefaultFeaturePage(feature);
                WriteFeaturePage(file, content);
            }
        }

        private static void WriteFeaturePage(string file, FeaturePageContent content)
        {
            StringBuilder text = new StringBuilder();
            text.Append("[Feature]\n")
                .Append("Title = \"").Append(EscapeIni(content.Title)).Append("\"\n")
                .Append("Summary = \"").Append(EscapeIni(content.Summary)).Append("\"\n")
                .Append("Overview = \"").Append(EscapeIni(content.Overview)).Append("\"\n");

            for (int i = 0; i < content.Usage.Count; i++)
            {
                text.Append("Usage").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(" = \"").Append(EscapeIni(content.Usage[i])).Append("\"\n");
            }

            for (int i = 0; i < content.Notes.Count; i++)
            {
                text.Append("Note").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(" = \"").Append(EscapeIni(content.Notes[i])).Append("\"\n");
            }

            File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
        }

        private void EnsureRegionContent(Scene scene)
        {
            string dir = GetRegionDirectory(scene);
            string mediaDir = Path.Combine(dir, "media");
            string postsDir = Path.Combine(dir, "posts");

            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(mediaDir);
            Directory.CreateDirectory(postsDir);

            string profile = Path.Combine(dir, "profile.ini");
            if (!File.Exists(profile))
            {
                File.WriteAllText(profile,
                    "[RegionWeb]\n"
                    + "Title = \"" + EscapeIni(scene.RegionInfo.RegionName) + "\"\n"
                    + "Tagline = \"News, photos and visitor information\"\n"
                    + "Description = \"Tell visitors what makes this region special. Add JPEG or PNG files to the media folder, then list them in Gallery.\"\n"
                    + "HeroImage = \"\"\n"
                    + "; Gallery entries use filename|caption, separated by semicolons.\n"
                    + "Gallery = \"\"\n",
                    new UTF8Encoding(false));
            }

            string post = Path.Combine(postsDir, "welcome.txt");
            if (!File.Exists(post))
            {
                File.WriteAllText(post,
                    "Title: Welcome to " + scene.RegionInfo.RegionName + "\n"
                    + "Date: " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n"
                    + "Summary: First public note for this region.\n"
                    + "Image: \n"
                    + "----\n"
                    + "This is the first Vanilla Sim post. Replace this text with news, build notes, events, credits, or travel information for visitors.\n",
                    new UTF8Encoding(false));
            }
        }

        private void AppendPostSummary(StringBuilder html, string slug, BlogPost post)
        {
            html.Append("<article class=\"post\">");
            if (!string.IsNullOrEmpty(post.Image))
                html.Append("<img src=\"").Append(Html(MediaURL(slug, post.Image))).Append("\" alt=\"\">");

            html.Append("<time>").Append(Html(FormatDate(post.Date))).Append("</time>")
                .Append("<h3><a href=\"").Append(Html(m_basePath)).Append("/").Append(Url(slug))
                .Append("/post/").Append(Url(post.Slug)).Append("\">").Append(Html(post.Title)).Append("</a></h3>")
                .Append("<p>").Append(Html(post.Summary)).Append("</p>")
                .Append("</article>");
        }

        private void AppendStats(StringBuilder html, RegionWebStats stats)
        {
            html.Append("<section class=\"stats\"><h2>Live Stats</h2><dl>")
                .Append(Stat("Avatars", stats.RootAgents.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Child Agents", stats.ChildAgents.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("NPCs", stats.NPCs.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Objects", stats.Objects.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Prims", stats.Prims.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Mesh Parts", stats.MeshParts.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Sculpt Parts", stats.SculptParts.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Parcels", stats.ParcelCount.ToString(CultureInfo.InvariantCulture)))
                .Append(Stat("Sim FPS", stats.SimFPS.ToString("0.0", CultureInfo.InvariantCulture)))
                .Append("</dl></section>");
        }

        private void AppendEconomy(StringBuilder html, Scene scene)
        {
            IMoneyModule money = scene.RequestModuleInterface<IMoneyModule>();
            if (money == null)
                return;

            MethodInfo method = money.GetType().GetMethod("GetCurrencyStats", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                return;

            IDictionary<string, string> stats;
            try
            {
                stats = method.Invoke(money, null) as IDictionary<string, string>;
            }
            catch
            {
                return;
            }

            if (stats == null || stats.Count == 0)
                return;

            html.Append("<section class=\"stats\"><h2>Economy</h2><dl>");
            foreach (KeyValuePair<string, string> entry in stats)
                html.Append(Stat(entry.Key, entry.Value));
            html.Append("</dl><p><a href=\"")
                .Append(Html(m_basePath)).Append("/currency/\">Open avatar wallet</a></p></section>");
        }

        private void AppendParcels(StringBuilder html, RegionWebStats stats)
        {
            html.Append("<section class=\"parcels\"><h2>Largest Parcels</h2>");
            foreach (ParcelSummary parcel in stats.Parcels)
            {
                html.Append("<div><strong>").Append(Html(parcel.Name)).Append("</strong><span>")
                    .Append(parcel.Area.ToString(CultureInfo.InvariantCulture)).Append(" m2</span></div>");
            }
            html.Append("</section>");
        }

        private string GetRegionDirectory(Scene scene)
        {
            return Path.Combine(m_absoluteContentDirectory, MakeSlug(scene.RegionInfo.RegionName));
        }

        private string BuildEstateCarousel(IEnumerable<Scene> scenes)
        {
            string inventoryCarousel = BuildInventoryCarousel(scenes);
            if (!string.IsNullOrEmpty(inventoryCarousel))
                return inventoryCarousel;

            return BuildEstateMapCarousel(scenes);
        }

        private string BuildRegionCarousel(Scene scene)
        {
            List<InventoryCarouselItem> items = GetRegionInventoryCarouselItems(scene, true);
            return BuildInventoryCarouselMarkup(items, "#region-photos", "View " + scene.RegionInfo.RegionName + " photos", "region-inventory-snapshots", "Vanilla Sim region snapshot");
        }

        private string BuildInventoryCarousel(IEnumerable<Scene> scenes)
        {
            List<InventoryCarouselItem> items = GetEstateInventoryCarouselItems(scenes, true);
            return BuildInventoryCarouselMarkup(items, "#regions", "View Vanilla Sim regions", "inventory-snapshots", "Vanilla Sim snapshot");
        }

        private string BuildInventoryCarouselMarkup(List<InventoryCarouselItem> items, string href, string ariaLabel, string carouselName, string fallbackAlt)
        {
            if (items == null || items.Count == 0)
                return string.Empty;

            StringBuilder slides = new StringBuilder();
            int count = 0;

            foreach (InventoryCarouselItem item in items)
            {
                slides.Append("<a class=\"estate-slide");
                if (count == 0)
                    slides.Append(" is-active");
                slides.Append("\" href=\"").Append(Html(href)).Append("\" aria-label=\"")
                    .Append(Html(ariaLabel)).Append("\"><img src=\"")
                    .Append(Html(m_basePath)).Append("/inventory-carousel/")
                    .Append(Html(item.AssetID.ToString())).Append(".jpg\" alt=\"")
                    .Append(Html(string.IsNullOrWhiteSpace(item.Name) ? fallbackAlt : item.Name)).Append("\"");
                if (count > 0)
                    slides.Append(" loading=\"lazy\"");
                slides.Append("></a>");
                count++;
            }

            return "<div class=\"estate-carousel\" data-carousel=\"" + Html(carouselName) + "\">" + slides
                + "<div class=\"estate-carousel-shade\" aria-hidden=\"true\"></div></div>";
        }

        private static string BuildSingleHeroImage(string imageURL, string altText)
        {
            if (string.IsNullOrEmpty(imageURL))
                return string.Empty;

            return "<div class=\"estate-carousel hero-single\"><span class=\"estate-slide is-active\"><img src=\""
                + Html(imageURL) + "\" alt=\"" + Html(altText) + "\"></span><div class=\"estate-carousel-shade\" aria-hidden=\"true\"></div></div>";
        }

        private List<InventoryCarouselItem> GetEstateInventoryCarouselItems(IEnumerable<Scene> scenes, bool createFolders)
        {
            List<InventoryCarouselItem> items = new List<InventoryCarouselItem>();
            if (!m_inventoryCarouselEnabled || scenes == null || string.IsNullOrWhiteSpace(m_inventoryCarouselFolder))
                return items;

            HashSet<UUID> owners = new HashSet<UUID>();
            HashSet<UUID> assets = new HashSet<UUID>();

            foreach (Scene scene in scenes.OrderBy(s => s.RegionInfo.RegionName))
            {
                UUID ownerID = GetRegionOwnerID(scene);
                if (ownerID == UUID.Zero || !owners.Add(ownerID))
                    continue;

                InventoryFolderBase folder = FindInventoryCarouselFolder(scene, ownerID, m_inventoryCarouselFolder, createFolders);
                if (folder == null)
                    continue;

                InventoryCollection content = GetInventoryFolderContent(scene, ownerID, folder.ID);
                if (content == null || content.Items == null)
                    continue;

                foreach (InventoryItemBase item in content.Items)
                {
                    if (!IsInventoryCarouselItem(item) || !assets.Add(item.AssetID))
                        continue;

                    items.Add(new InventoryCarouselItem
                    {
                        AssetID = item.AssetID,
                        Name = item.Name,
                        CreationDate = item.CreationDate
                    });
                }
            }

            return items
                .OrderByDescending(i => i.CreationDate)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(m_inventoryCarouselLimit)
                .ToList();
        }

        private List<InventoryCarouselItem> GetRegionInventoryCarouselItems(Scene scene, bool createFolder)
        {
            List<InventoryCarouselItem> items = new List<InventoryCarouselItem>();
            if (!m_inventoryCarouselEnabled || scene == null)
                return items;

            UUID ownerID = GetRegionOwnerID(scene);
            if (ownerID == UUID.Zero)
                return items;

            string folderName = GetRegionInventoryCarouselFolderName(scene);
            if (string.IsNullOrWhiteSpace(folderName))
                return items;

            InventoryFolderBase folder = FindInventoryCarouselFolder(scene, ownerID, folderName, createFolder);
            if (folder == null)
                return items;

            InventoryCollection content = GetInventoryFolderContent(scene, ownerID, folder.ID);
            if (content == null || content.Items == null)
                return items;

            HashSet<UUID> assets = new HashSet<UUID>();
            foreach (InventoryItemBase item in content.Items)
            {
                if (!IsInventoryCarouselItem(item) || !assets.Add(item.AssetID))
                    continue;

                items.Add(new InventoryCarouselItem
                {
                    AssetID = item.AssetID,
                    Name = item.Name,
                    CreationDate = item.CreationDate
                });
            }

            return items
                .OrderByDescending(i => i.CreationDate)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(m_inventoryCarouselLimit)
                .ToList();
        }

        private bool TryFindInventoryCarouselItem(UUID assetID, out Scene scene, out InventoryItemBase item)
        {
            scene = null;
            item = null;

            if (assetID == UUID.Zero || !m_inventoryCarouselEnabled)
                return false;

            HashSet<string> checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Scene candidateScene in GetSceneSnapshot().OrderBy(s => s.RegionInfo.RegionName))
            {
                UUID ownerID = GetRegionOwnerID(candidateScene);
                if (ownerID == UUID.Zero)
                    continue;

                List<string> folderNames = new List<string>();
                folderNames.Add(m_inventoryCarouselFolder);
                string regionFolderName = GetRegionInventoryCarouselFolderName(candidateScene);
                if (!string.Equals(regionFolderName, m_inventoryCarouselFolder, StringComparison.OrdinalIgnoreCase))
                    folderNames.Add(regionFolderName);

                foreach (string folderName in folderNames)
                {
                    if (string.IsNullOrWhiteSpace(folderName))
                        continue;

                    string folderKey = ownerID.ToString() + ":" + folderName;
                    if (!checkedFolders.Add(folderKey))
                        continue;

                    InventoryFolderBase folder = FindInventoryCarouselFolder(candidateScene, ownerID, folderName, false);
                    if (folder == null)
                        continue;

                    InventoryCollection content = GetInventoryFolderContent(candidateScene, ownerID, folder.ID);
                    if (content == null || content.Items == null)
                        continue;

                    foreach (InventoryItemBase candidateItem in content.Items)
                    {
                        if (IsInventoryCarouselItem(candidateItem) && candidateItem.AssetID == assetID)
                        {
                            scene = candidateScene;
                            item = candidateItem;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private InventoryFolderBase FindInventoryCarouselFolder(Scene scene, UUID ownerID, string folderName, bool createIfMissing)
        {
            if (scene == null || scene.InventoryService == null || ownerID == UUID.Zero || string.IsNullOrWhiteSpace(folderName))
                return null;

            InventoryFolderBase root = null;
            try
            {
                root = scene.InventoryService.GetRootFolder(ownerID);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[REGION WEB]: Could not read inventory root for carousel owner {0}: {1}", ownerID, e.Message);
                return null;
            }

            if (root == null)
                return null;

            Queue<InventoryFolderBase> pending = new Queue<InventoryFolderBase>();
            HashSet<UUID> visited = new HashSet<UUID>();
            pending.Enqueue(root);

            while (pending.Count > 0 && visited.Count < InventoryCarouselFolderSearchLimit)
            {
                InventoryFolderBase folder = pending.Dequeue();
                if (folder == null || !visited.Add(folder.ID))
                    continue;

                if (string.Equals(folder.Name, folderName, StringComparison.OrdinalIgnoreCase))
                    return folder;

                InventoryCollection content = GetInventoryFolderContent(scene, ownerID, folder.ID);
                if (content == null || content.Folders == null)
                    continue;

                foreach (InventoryFolderBase child in content.Folders)
                    pending.Enqueue(child);
            }

            return createIfMissing ? CreateInventoryCarouselFolder(scene, ownerID, root, folderName) : null;
        }

        private InventoryFolderBase CreateInventoryCarouselFolder(Scene scene, UUID ownerID, InventoryFolderBase root, string folderName)
        {
            if (scene == null || scene.InventoryService == null || ownerID == UUID.Zero || root == null || string.IsNullOrWhiteSpace(folderName))
                return null;

            InventoryFolderBase folder = new InventoryFolderBase(UUID.Random(), folderName, ownerID, (short)FolderType.None, root.ID, root.Version);
            try
            {
                if (scene.InventoryService.AddFolder(folder))
                {
                    m_log.InfoFormat("[REGION WEB]: Created inventory carousel folder \"{0}\" for owner {1}", folderName, ownerID);
                    return folder;
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[REGION WEB]: Could not create inventory carousel folder \"{0}\" for owner {1}: {2}", folderName, ownerID, e.Message);
            }

            return null;
        }

        private void EnsureInventoryCarouselFolders(Scene scene)
        {
            if (!m_inventoryCarouselEnabled || scene == null)
                return;

            UUID ownerID = GetRegionOwnerID(scene);
            if (ownerID == UUID.Zero)
                return;

            FindInventoryCarouselFolder(scene, ownerID, m_inventoryCarouselFolder, true);

            string regionFolderName = GetRegionInventoryCarouselFolderName(scene);
            if (!string.Equals(regionFolderName, m_inventoryCarouselFolder, StringComparison.OrdinalIgnoreCase))
                FindInventoryCarouselFolder(scene, ownerID, regionFolderName, true);
        }

        private string GetRegionInventoryCarouselFolderName(Scene scene)
        {
            string regionName = scene != null && scene.RegionInfo != null ? scene.RegionInfo.RegionName : "Region";
            string folderName = m_regionInventoryCarouselFolderTemplate.Replace("{RegionName}", regionName);
            return folderName.Trim();
        }

        private InventoryCollection GetInventoryFolderContent(Scene scene, UUID ownerID, UUID folderID)
        {
            if (scene == null || scene.InventoryService == null)
                return null;

            try
            {
                return scene.InventoryService.GetFolderContent(ownerID, folderID);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[REGION WEB]: Could not read inventory carousel folder {0}: {1}", folderID, e.Message);
                return null;
            }
        }

        private static UUID GetRegionOwnerID(Scene scene)
        {
            if (scene == null || scene.RegionInfo == null || scene.RegionInfo.EstateSettings == null)
                return UUID.Zero;

            return scene.RegionInfo.EstateSettings.EstateOwner;
        }

        private static bool IsInventoryCarouselItem(InventoryItemBase item)
        {
            if (item == null || item.AssetID == UUID.Zero)
                return false;

            int assetType = item.AssetType;
            int invType = item.InvType;
            return assetType == (int)AssetType.Texture
                || assetType == (int)AssetType.TextureTGA
                || invType == (int)InventoryType.Snapshot
                || invType == (int)InventoryType.Texture;
        }

        private bool TryParseInventoryCarouselAssetID(string unsafeName, out UUID assetID)
        {
            string fileName = Path.GetFileName(unsafeName ?? string.Empty);
            string rawID = Path.GetFileNameWithoutExtension(fileName);
            return UUID.TryParse(rawID, out assetID);
        }

        private bool TryGetCachedInventoryCarouselAsset(UUID assetID, out byte[] data, out string contentType)
        {
            data = null;
            contentType = string.Empty;
            if (m_inventoryCarouselCacheSeconds <= 0)
                return false;

            lock (m_inventoryCarouselCacheLock)
            {
                if (!m_inventoryCarouselAssetCache.TryGetValue(assetID, out InventoryCarouselAssetCacheEntry entry))
                    return false;

                if (entry.ExpiresUTC <= DateTime.UtcNow)
                {
                    m_inventoryCarouselAssetCache.Remove(assetID);
                    return false;
                }

                data = entry.Data;
                contentType = entry.ContentType;
                return data != null && data.Length > 0;
            }
        }

        private void SetCachedInventoryCarouselAsset(UUID assetID, byte[] data, string contentType)
        {
            if (m_inventoryCarouselCacheSeconds <= 0 || data == null || data.Length == 0)
                return;

            lock (m_inventoryCarouselCacheLock)
            {
                m_inventoryCarouselAssetCache[assetID] = new InventoryCarouselAssetCacheEntry
                {
                    Data = data,
                    ContentType = contentType,
                    ExpiresUTC = DateTime.UtcNow.AddSeconds(m_inventoryCarouselCacheSeconds)
                };
            }
        }

        private void SendInventoryCarouselImageResponse(IOSHttpResponse response, byte[] data, string contentType)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = string.IsNullOrEmpty(contentType) ? "image/jpeg" : contentType;
            if (m_inventoryCarouselCacheSeconds > 0)
                response.AddHeader("Cache-Control", "public, max-age=" + m_inventoryCarouselCacheSeconds.ToString(CultureInfo.InvariantCulture));
            response.RawBuffer = data;
        }

        private bool TryEncodeInventoryCarouselAsset(Scene scene, AssetBase asset, out byte[] data, out string contentType)
        {
            data = null;
            contentType = string.Empty;

            if (asset == null || asset.Data == null || asset.Data.Length == 0)
                return false;

            if (TryGetBrowserImageContentType(asset.Data, out contentType))
            {
                data = asset.Data;
                return true;
            }

            OpenMetaverse.Imaging.ManagedImage managedImage = null;
            System.Drawing.Image image = null;
            try
            {
                IJ2KDecoder decoder = scene == null ? null : scene.RequestModuleInterface<IJ2KDecoder>();
                if (decoder != null)
                    image = decoder.DecodeToImage(asset.Data);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[REGION WEB]: IJ2KDecoder failed for inventory carousel asset {0}: {1}", asset.ID, e.Message);
            }

            if (image == null)
            {
                try
                {
                    System.Drawing.Image decodedImage;
                    if (OpenMetaverse.Imaging.OpenJPEG.DecodeToImage(asset.Data, out managedImage, out decodedImage))
                        image = decodedImage;
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[REGION WEB]: OpenJPEG failed for inventory carousel asset {0}: {1}", asset.ID, e.Message);
                }
            }

            if (image == null)
            {
                if (managedImage != null)
                    managedImage.Clear();
                return false;
            }

            try
            {
                using (image)
                using (MemoryStream stream = new MemoryStream())
                {
                    image.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
                    data = stream.ToArray();
                    contentType = "image/jpeg";
                    return data.Length > 0;
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[REGION WEB]: JPEG encode failed for inventory carousel asset {0}: {1}", asset.ID, e.Message);
                return false;
            }
            finally
            {
                if (managedImage != null)
                    managedImage.Clear();
            }
        }

        private static bool TryGetBrowserImageContentType(byte[] data, out string contentType)
        {
            contentType = string.Empty;
            if (data == null || data.Length < 4)
                return false;

            if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
            {
                contentType = "image/jpeg";
                return true;
            }

            if (data.Length >= 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4e && data[3] == 0x47
                && data[4] == 0x0d && data[5] == 0x0a && data[6] == 0x1a && data[7] == 0x0a)
            {
                contentType = "image/png";
                return true;
            }

            if (data.Length >= 6
                && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46
                && data[3] == 0x38 && (data[4] == 0x37 || data[4] == 0x39) && data[5] == 0x61)
            {
                contentType = "image/gif";
                return true;
            }

            if (data.Length >= 12
                && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            {
                contentType = "image/webp";
                return true;
            }

            return false;
        }

        private string BuildEstateMapCarousel(IEnumerable<Scene> scenes)
        {
            if (scenes == null)
                return string.Empty;

            StringBuilder slides = new StringBuilder();
            int count = 0;

            foreach (Scene scene in scenes.OrderBy(s => s.RegionInfo.RegionName))
            {
                string slug = MakeSlug(scene.RegionInfo.RegionName);
                string imageURL = GetMapURL(scene);
                if (string.IsNullOrEmpty(imageURL))
                    continue;

                slides.Append("<a class=\"estate-slide");
                if (count == 0)
                    slides.Append(" is-active");
                slides.Append("\" href=\"")
                    .Append(Html(m_basePath)).Append("/").Append(Url(slug)).Append("/\" aria-label=\"View ")
                    .Append(Html(scene.RegionInfo.RegionName)).Append("\"><img src=\"")
                    .Append(Html(imageURL)).Append("\" alt=\"")
                    .Append(Html(scene.RegionInfo.RegionName)).Append(" map\"");
                if (count > 0)
                    slides.Append(" loading=\"lazy\"");
                slides.Append("></a>");
                count++;
            }

            if (count == 0)
                return string.Empty;

            return "<div class=\"estate-carousel\" data-carousel=\"region-maps\">" + slides
                + "<div class=\"estate-carousel-shade\" aria-hidden=\"true\"></div></div>";
        }

        private string GetHeroURL(Scene scene, RegionPageContent content)
        {
            string slug = MakeSlug(scene.RegionInfo.RegionName);
            if (!string.IsNullOrEmpty(content.HeroImage))
                return MediaURL(slug, content.HeroImage);

            if (m_showMap)
                return GetMapURL(scene);

            return string.Empty;
        }

        private string GetMapURL(Scene scene)
        {
            string regionImage = "regionImage" + scene.RegionInfo.RegionID.ToString().Replace("-", "");
            return "/index.php?method=" + regionImage;
        }

        private string MediaURL(string slug, string fileName)
        {
            return m_basePath + "/" + Url(slug) + "/media/" + Url(Path.GetFileName(fileName));
        }

        private string EstateMediaURL(string fileName)
        {
            return m_basePath + "/media/" + Url(Path.GetFileName(fileName));
        }

        private string FeatureURL(FeatureItem feature)
        {
            return m_basePath + "/feature/" + Url(MakeSlug(feature.Title)) + "/";
        }

        private static List<FeatureItem> ParseFeatures(string features)
        {
            List<FeatureItem> items = new List<FeatureItem>();
            foreach (string entry in features.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(new[] { '|' }, 2);
                string title = parts[0].Trim();
                if (string.IsNullOrEmpty(title))
                    continue;

                items.Add(new FeatureItem
                {
                    Title = title,
                    Body = parts.Length > 1 ? parts[1].Trim() : string.Empty
                });
            }

            return items;
        }

        private static List<string> ParseFeatureList(IConfig config, string prefix)
        {
            List<string> items = new List<string>();
            for (int i = 1; i <= 12; i++)
            {
                string value = config.GetString(prefix + i.ToString(CultureInfo.InvariantCulture), string.Empty).Trim();
                if (!string.IsNullOrEmpty(value))
                    items.Add(value);
            }

            string joined = config.GetString(prefix, string.Empty);
            if (string.IsNullOrEmpty(joined))
                joined = config.GetString(prefix + "s", string.Empty);

            foreach (string value in joined.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = value.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    items.Add(trimmed);
            }

            return items;
        }

        private static List<FeatureItem> ParseNumberedFeatures(IConfig config)
        {
            List<FeatureItem> items = new List<FeatureItem>();
            for (int i = 1; i <= 20; i++)
            {
                string entry = config.GetString("Feature" + i.ToString(CultureInfo.InvariantCulture), string.Empty);
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                List<FeatureItem> parsed = ParseFeatures(entry);
                if (parsed.Count > 0)
                    items.Add(parsed[0]);
            }

            return items;
        }

        private static List<FeatureItem> NormalizeFeatures(List<FeatureItem> features)
        {
            List<FeatureItem> normalized = new List<FeatureItem>();
            bool mapFeatureAdded = false;
            bool scriptEngineFeatureAdded = false;
            bool currencyFeatureAdded = false;

            foreach (FeatureItem feature in features)
            {
                if (IsWorldMapFeature(feature.Title))
                {
                    if (!mapFeatureAdded)
                    {
                        normalized.Add(new FeatureItem
                        {
                            Title = "High quality world map",
                            Body = "Terrain textures, water depth shading, land detail, aerial tone mapping, mesh/sculpt geometry projection, cleaner water alpha handling, background generation and cooperative rendering make map tiles sharper, more geographic and safer for simulator responsiveness."
                        });
                        mapFeatureAdded = true;
                    }

                    continue;
                }

                if (IsScriptEngineFeature(feature.Title))
                {
                    if (!scriptEngineFeatureAdded)
                    {
                        normalized.Add(new FeatureItem
                        {
                            Title = ScriptEngineFeatureTitle,
                            Body = ScriptEngineFeatureBody
                        });
                        scriptEngineFeatureAdded = true;
                    }

                    continue;
                }

                if (IsCurrencyFeature(feature.Title))
                {
                    if (!currencyFeatureAdded)
                    {
                        normalized.Add(new FeatureItem
                        {
                            Title = CurrencyFeatureTitle,
                            Body = CurrencyFeatureBody
                        });
                        currencyFeatureAdded = true;
                    }

                    continue;
                }

                if (feature.Title.Equals("Text build tools", StringComparison.OrdinalIgnoreCase))
                {
                    normalized.Add(new FeatureItem
                    {
                        Title = "AI-connected text build tools",
                        Body = "Estate builders can use text commands connected to AI or uploaded cartography textures to plan, generate and refine terrain or building ideas directly from the simulator workflow."
                    });
                    continue;
                }

                normalized.Add(feature);
            }

            return normalized;
        }

        private static bool IsWorldMapFeature(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            string normalized = title.Trim().ToLowerInvariant();
            return normalized == "high quality world map"
                || normalized == "mesh and sculpt aware map rendering"
                || normalized == "mesh and sculpt aware rendering"
                || normalized == "cleaner water and alpha handling"
                || normalized == "cleaner water overlays"
                || normalized == "background map generation"
                || normalized == "cooperative heavy rendering";
        }

        private static bool IsScriptEngineFeature(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            string normalized = title.Trim().ToLowerInvariant();
            return normalized == "second life-style script engine"
                || normalized == "second life-style scripting"
                || normalized == "second-life-style script engine"
                || normalized == "second life compatible script engine"
                || normalized == "experience-lite script permissions"
                || normalized == "experience-lite key-value store"
                || normalized == "experience-lite script engine"
                || normalized == "script engine"
                || normalized == "lsl script engine"
                || normalized == "linkset data"
                || normalized == "linkset data store"
                || normalized == "gltf render materials"
                || normalized == "pbr render materials"
                || normalized == "render material scripting"
                || normalized == "material primitive params"
                || normalized == "physics material scripting"
                || normalized == "parcel media scripting"
                || normalized == "inventory transfer scripting"
                || normalized == "parameterized rez"
                || normalized == "lsl secure hashing"
                || normalized == "scripted sit controls";
        }

        private static bool IsCurrencyFeature(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            string normalized = title.Trim().ToLowerInvariant();
            return normalized == "viewer-visible local currency"
                || normalized == "local currency"
                || normalized == "local currency economy"
                || normalized == "currency"
                || normalized == "economy"
                || normalized == "money module"
                || normalized == "viewer balance"
                || normalized == "viewer-visible currency";
        }

        private static bool IsMultiGridFeature(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            string normalized = title.Trim().ToLowerInvariant();
            return normalized == "multi-grid region attachments"
                || normalized == "multi-grid attachments"
                || normalized == "multigrid attachments"
                || normalized == "multigrid"
                || normalized == "multi-grid"
                || normalized == "secondary grid attachments";
        }

        private static void AddDefaultFeatures(List<FeatureItem> features)
        {
            features.Add(new FeatureItem
            {
                Title = "High quality world map",
                Body = "Terrain textures, water depth shading, land detail, aerial tone mapping, mesh/sculpt geometry projection, cleaner water alpha handling, background generation and cooperative rendering make map tiles sharper, more geographic and safer for simulator responsiveness."
            });
            features.Add(new FeatureItem
            {
                Title = "Vanilla Sim estate portal",
                Body = "Every region can have a public web page with photos, blog posts, map tile, parcels and live region statistics."
            });
            features.Add(new FeatureItem
            {
                Title = "Weather module",
                Body = "Regions can run rain, storm, snow or sunny presets, with wind, clouds, lightning, thunder and automatic forecast cycling."
            });
            features.Add(new FeatureItem
            {
                Title = "Wave-following boats",
                Body = "Boats can now move with the sea surface, following wave motion for a more natural marina and sailing experience."
            });
            features.Add(new FeatureItem
            {
                Title = "Smooth region crossings",
                Body = "Avatar and vehicle crossings between neighbouring regions are smoothed to reduce the hard stop, rubber-banding and visual pop of stock OpenSim border transfers."
            });
            features.Add(new FeatureItem
            {
                Title = "Lag-resistant walk animations",
                Body = "Walking animations recover cleanly after lag spikes, so avatars do not remain stuck in broken walk states when the simulator catches up."
            });
            features.Add(new FeatureItem
            {
                Title = "AI-connected text build tools",
                Body = "Estate builders can use text commands connected to AI or uploaded cartography textures to plan, generate and refine terrain or building ideas directly from the simulator workflow."
            });
            features.Add(new FeatureItem
            {
                Title = "Automatic cloud avatar recovery",
                Body = "If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds."
            });
            features.Add(new FeatureItem
            {
                Title = "Group auto invite",
                Body = "Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects."
            });
            features.Add(new FeatureItem
            {
                Title = CurrencyFeatureTitle,
                Body = CurrencyFeatureBody
            });
            features.Add(new FeatureItem
            {
                Title = MultiGridFeatureTitle,
                Body = MultiGridFeatureBody
            });
            features.Add(new FeatureItem
            {
                Title = "Viewer polish",
                Body = "Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent."
            });
            features.Add(new FeatureItem
            {
                Title = ScriptEngineFeatureTitle,
                Body = ScriptEngineFeatureBody
            });
        }

        private static void EnsureFeature(List<FeatureItem> features, string title, string body)
        {
            foreach (FeatureItem feature in features)
            {
                if (feature.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsScriptEngineFeature(title) || IsCurrencyFeature(title) || IsMultiGridFeature(title))
                        feature.Body = body;
                    return;
                }
            }

            features.Add(new FeatureItem
            {
                Title = title,
                Body = body
            });
        }

        private static string Stat(string label, string value)
        {
            return "<dt>" + Html(label) + "</dt><dd>" + Html(value) + "</dd>";
        }

        private static void AppendFeatureList(StringBuilder html, string title, List<string> items)
        {
            if (items.Count == 0)
                return;

            html.Append("<section><h2>").Append(Html(title)).Append("</h2><ul>");
            foreach (string item in items)
                html.Append("<li>").Append(Html(item)).Append("</li>");
            html.Append("</ul></section>");
        }

        private static void SendHtml(IOSHttpResponse response, string html)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/html; charset=utf-8";
            response.RawBuffer = Encoding.UTF8.GetBytes(html);
        }

        private static void SendNotFound(IOSHttpResponse response, string message)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.ContentType = "text/plain";
            response.RawBuffer = Encoding.UTF8.GetBytes(message);
        }

        private static string RegionWebCss()
        {
            StringBuilder css = new StringBuilder(8192);
            css.Append(":root{--ink:#05070a;--paper:#f4f7f9;--card:#fff;--text:#111820;--muted:#68727c;--line:#dfe7eb;--dark:#11161b;--dark2:#1d2227;--accent:#12bdf4;--accent2:#c700ff;--shadow:0 22px 60px rgba(5,10,15,.14)}")
                .Append("html{scroll-behavior:smooth;scroll-padding-top:88px}body{margin:0;background:var(--paper);color:var(--text);font:16px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}a{color:#0079b6;text-decoration:none}a:hover{color:#00aeea}img{max-width:100%;display:block}.wrap{max-width:1320px;margin:0 auto;padding:0 28px}")
                .Append(".site-nav{position:sticky;top:0;z-index:1000;background:#020304;border-bottom:2px solid var(--accent);box-shadow:0 14px 40px rgba(0,0,0,.32)}.nav-wrap{display:flex;align-items:center;justify-content:space-between;gap:28px;min-height:68px}.site-nav a{color:#f6f7f8;font-weight:900}.brand{display:flex;align-items:center;gap:13px;color:#fff;min-width:190px}.brand-mark{position:relative;width:52px;height:52px;flex:0 0 52px;border:3px solid var(--accent);border-radius:14px;background:linear-gradient(135deg,rgba(18,189,244,.18),rgba(199,0,255,.14));box-shadow:0 0 0 1px rgba(255,255,255,.08) inset,0 10px 28px rgba(18,189,244,.22);transform:rotate(-6deg);overflow:hidden}.brand-mark:before{content:'V';position:absolute;left:8px;top:4px;color:var(--accent);font-size:30px;line-height:1;font-weight:1000;transform:rotate(6deg)}.brand-mark:after{content:'S';position:absolute;right:7px;bottom:2px;color:#fff;font-size:30px;line-height:1;font-weight:1000;transform:rotate(6deg)}.brand-mark span{position:absolute;left:9px;right:9px;top:25px;height:3px;background:var(--accent);border-radius:999px;transform:rotate(-22deg)}.brand-mark span:before{content:'';position:absolute;right:-4px;top:-4px;width:11px;height:11px;background:var(--accent2);border-radius:50%;box-shadow:0 0 18px rgba(199,0,255,.5)}.brand-type{display:grid;text-transform:uppercase;line-height:.84;color:#fff}.brand-type span{font-size:16px;font-weight:1000;letter-spacing:.08em}.brand-type strong{font-size:35px;font-weight:1000;letter-spacing:0}.nav-links{display:flex;align-items:center;justify-content:flex-end;flex-wrap:wrap;gap:28px}.nav-links a{font-size:17px}.nav-links a:hover{color:var(--accent)}.nav-github{display:inline-flex;align-items:center;gap:8px}.nav-github svg{width:21px;height:21px;fill:currentColor}.nav-cta{background:var(--accent2);color:#fff!important;padding:11px 20px;border-radius:5px;box-shadow:0 12px 30px rgba(199,0,255,.24)}.nav-cta:hover{background:#a900e0!important;color:#fff!important}")
                .Append(".page-links{display:flex;flex-wrap:wrap;gap:10px;margin:0 0 22px}.page-links a,.back{display:inline-flex;align-items:center;min-height:38px;background:#fff;border:1px solid var(--line);border-radius:6px;color:#111820;padding:0 13px;font-weight:900;box-shadow:0 8px 22px rgba(12,18,24,.06)}.page-links a:hover,.back:hover{border-color:var(--accent);color:#0079b6}.estate-hero{position:relative;min-height:640px;background-size:cover;background-position:center;background-repeat:no-repeat;display:flex;align-items:center;color:#fff;overflow:hidden;background-color:#090d14}.estate-hero-plain{background:#090d14}.estate-carousel{position:absolute;inset:0;z-index:0;background:#090d14}.estate-slide{position:absolute;inset:0;opacity:0;transition:opacity 2.2s ease;transform:scale(1.025)}.estate-slide.is-active{opacity:1}.estate-slide img{width:100%;height:100%;object-fit:cover;filter:saturate(1.08) contrast(1.05)}.estate-carousel-shade{position:absolute;inset:0;background:linear-gradient(90deg,rgba(0,0,0,.78),rgba(0,0,0,.42) 48%,rgba(0,0,0,.30)),linear-gradient(0deg,rgba(3,8,12,.90),rgba(3,8,12,.10) 45%,rgba(3,8,12,.18));pointer-events:none}.estate-hero .wrap{position:relative;z-index:2;padding-top:118px;padding-bottom:88px}.estate-hero p{max-width:720px;color:#f2f6f8;font-size:21px}.estate-hero>div>p:first-child,.hero p,.feature-kicker{margin:0 0 12px;color:var(--accent);text-transform:uppercase;font-size:15px;font-weight:1000;letter-spacing:.08em}.estate-hero h1{max-width:790px;margin:0;color:#fff;font-size:76px;line-height:.92;text-transform:uppercase}.estate-actions{display:flex;flex-wrap:wrap;gap:12px;margin-top:30px}.estate-actions a{background:var(--accent2);color:#fff;padding:12px 18px;border-radius:5px;font-weight:1000;box-shadow:0 12px 32px rgba(199,0,255,.24)}.estate-actions a+a{background:#fff;color:#111820}.estate-actions a:hover{color:#fff;background:#a900e0}.estate-actions a+a:hover{color:#0079b6;background:#edf9ff}")
                .Append("main{background:var(--paper)}.estate-stats{display:grid;grid-template-columns:repeat(5,1fr);gap:14px;margin-top:-38px;position:relative;z-index:2}.estate-stats div{background:#fff;border:1px solid var(--line);border-radius:8px;padding:20px;box-shadow:var(--shadow)}.estate-stats strong{display:block;font-size:34px;line-height:1}.estate-stats span{color:var(--muted);font-weight:800}.feature-section{padding-top:58px}.feature-section h2,.list h2{font-size:36px;line-height:1.05;margin:0 0 22px}.feature-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:18px}.feature-card{display:block;background:#fff;border:1px solid var(--line);border-radius:8px;color:var(--text);padding:22px;min-height:190px;box-shadow:0 12px 36px rgba(5,10,15,.07)}.feature-card:hover{border-color:var(--accent);transform:translateY(-2px);transition:transform .16s ease,border-color .16s ease}.feature-card h3{margin:0 0 8px;font-size:22px}.feature-card p{margin:0;color:#56616a}.feature-card span{display:inline-block;margin-top:18px;color:#0079b6;font-weight:1000}.feature-page,.script-reference,.wallet-page{padding-top:50px;padding-bottom:78px}.feature-page{max-width:920px}.feature-page h1,.script-reference h1,.wallet-page h1{font-size:56px;line-height:1;margin:0 0 18px}.feature-page .lead,.script-reference .lead,.wallet-page .lead{font-size:22px;color:#45505a;margin:0 0 22px}.feature-page section{border-top:1px solid var(--line);padding-top:26px;margin-top:28px}.feature-page h2{font-size:30px;margin:0 0 12px}.feature-page li{margin:0 0 10px;color:#38424b}")
                .Append(".hero{position:relative;min-height:430px;background-size:cover;background-position:center;background-repeat:no-repeat;display:flex;align-items:flex-end;color:#fff;overflow:hidden;background-color:#090d14}.hero .wrap{position:relative;z-index:2;padding-top:100px;padding-bottom:54px}.hero h1{margin:0;color:#fff;font-size:64px;line-height:.95;text-transform:uppercase}.meta{margin-top:16px;color:#edf4f7}.layout{display:grid;grid-template-columns:minmax(0,1fr) 360px;gap:36px;padding-top:42px;padding-bottom:64px}.story{min-width:0}.story>p{font-size:19px;color:#34404a}.gallery{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:16px;margin:32px 0}.gallery figure{margin:0;background:#fff;border:1px solid var(--line);border-radius:8px;overflow:hidden;box-shadow:0 12px 34px rgba(5,10,15,.08)}.gallery img{aspect-ratio:4/3;object-fit:cover}.gallery figcaption{padding:11px;color:#59636c;font-size:14px}.panel{align-self:start}.map{width:100%;aspect-ratio:1;object-fit:cover;border-radius:8px;border:1px solid var(--line);box-shadow:var(--shadow)}.stats,.parcels{margin-top:18px;background:#fff;border:1px solid var(--line);border-radius:8px;padding:20px;box-shadow:0 12px 34px rgba(5,10,15,.07)}.stats h2,.parcels h2,.story h2{margin:0 0 14px}.stats dl{display:grid;grid-template-columns:1fr auto;gap:8px 16px;margin:0}.stats dt{color:var(--muted)}.stats dd{margin:0;font-weight:900}.parcels div{display:flex;justify-content:space-between;gap:12px;border-top:1px solid var(--line);padding:10px 0}.parcels div:first-of-type{border-top:0}.parcels span{color:var(--muted)}")
                .Append(".post{border-top:1px solid var(--line);padding:24px 0}.post img{width:100%;max-height:380px;object-fit:cover;margin-bottom:14px;border-radius:8px}.post time{color:var(--muted);font-size:13px}.post h3{margin:4px 0 8px;font-size:25px}.post p{color:#46515a}.post-page{padding-top:42px;padding-bottom:68px;max-width:860px}.post.full h1{font-size:48px;line-height:1.05;margin:6px 0 22px}.post.full p{font-size:18px}.region-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:20px}.list{padding-top:50px;padding-bottom:70px}.region-card{background:#fff;border:1px solid var(--line);border-radius:8px;overflow:hidden;color:var(--text);box-shadow:0 12px 36px rgba(5,10,15,.08)}.region-card:hover{border-color:var(--accent);transform:translateY(-2px);transition:transform .16s ease,border-color .16s ease}.region-card img{aspect-ratio:16/9;object-fit:cover}.region-card strong,.region-card span{display:block;padding:0 16px}.region-card strong{padding-top:15px;font-size:21px}.region-card span{padding-bottom:16px;color:#59636c}.empty code{word-break:break-all}")
                .Append(".script-source{max-width:880px;color:#52606b}.script-toc{border-top:1px solid var(--line);margin-top:32px;padding-top:24px}.script-toc h2,.script-group h2{font-size:30px;margin:0 0 14px}.script-toc div{display:flex;flex-wrap:wrap;gap:10px}.script-toc a{background:#fff;border:1px solid var(--line);border-radius:6px;padding:9px 12px;color:#111820;font-weight:900}.script-toc span{color:#0079b6}.script-group{border-top:1px solid var(--line);margin-top:32px;padding-top:26px}.script-card{background:#fff;border:1px solid var(--line);border-radius:8px;padding:20px;margin:0 0 16px;box-shadow:0 12px 34px rgba(5,10,15,.07)}.script-card-head{display:flex;align-items:flex-start;justify-content:space-between;gap:18px}.script-card h3{font-size:23px;margin:0}.script-card-head span{color:#67727b;font-size:13px;text-align:right}.signature{margin:12px 0;color:#111820}.signature code,.script-card pre{background:#0d1115;border:1px solid #252d35;border-radius:6px}.signature code{display:block;overflow:auto;padding:11px;color:#eef7fb}.script-detail{margin:8px 0;color:#424d56}.script-detail strong{color:#111820}.script-card details{margin-top:12px}.script-card summary{cursor:pointer;color:#0079b6;font-weight:1000}.script-card pre{overflow:auto;padding:12px;color:#dfeaf0}.script-focus{border-top:1px solid var(--line);margin-top:30px;padding-top:26px}")
                .Append(".wallet-guide{display:flex;align-items:center;justify-content:space-between;gap:18px;background:#fff;border:1px solid var(--line);border-radius:8px;padding:20px;margin:22px 0 4px;box-shadow:0 12px 34px rgba(5,10,15,.07)}.wallet-guide span{display:block;color:var(--accent);font-size:13px;font-weight:1000;letter-spacing:.08em;text-transform:uppercase}.wallet-guide h2{margin:4px 0 6px;font-size:25px}.wallet-guide p{margin:0;color:#56616a}.wallet-guide a{flex:0 0 auto;background:#020304;color:#fff;border:1px solid var(--accent);border-radius:5px;padding:10px 14px;font-weight:1000}.wallet-guide a:hover{background:var(--accent);color:#020304}")
                .Append(".wallet-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(290px,1fr));gap:18px;margin-top:26px}.wallet-card,.wallet-summary{background:#fff;border:1px solid var(--line);border-radius:8px;padding:20px;box-shadow:0 12px 34px rgba(5,10,15,.07)}.wallet-card h2{margin:0 0 14px;font-size:25px}.wallet-card label{display:block;color:#46515a;font-weight:900;margin:0 0 12px}.wallet-card input{box-sizing:border-box;width:100%;margin-top:6px;background:#f8fbfc;border:1px solid #cfdce2;color:#111820;border-radius:6px;padding:11px;font:inherit}.wallet-card button,.wallet-logout button{border:0;border-radius:5px;background:var(--accent2);color:#fff;padding:11px 15px;font-weight:1000;cursor:pointer}.wallet-card button:hover,.wallet-logout button:hover{background:#a900e0}.wallet-note{color:#65717a;margin:12px 0 0}.wallet-message{border:1px solid #b9e7c4;background:#ecfff1;color:#145923;border-radius:6px;padding:12px 14px}.wallet-message.error{border-color:#f0b6b6;background:#fff0f0;color:#8a1d1d}.wallet-summary{display:grid;grid-template-columns:repeat(3,1fr);gap:1px;margin-top:24px;padding:0;overflow:hidden}.wallet-summary div{background:#fff;padding:18px}.wallet-summary span{display:block;color:#65717a}.wallet-summary strong{display:block;font-size:28px;word-break:break-word}.wallet-statement{margin-top:18px}.wallet-table{overflow:auto;background:#fff;border-radius:8px;border:1px solid var(--line)}.wallet-table table{width:100%;border-collapse:collapse}.wallet-table th,.wallet-table td{text-align:left;border-top:1px solid var(--line);padding:10px;white-space:nowrap}.wallet-table th{background:#f0f5f7}.wallet-table td:last-child{white-space:normal}.wallet-table .credit{color:#128c3b}.wallet-table .debit{color:#b92828}.wallet-logout{margin-top:18px}")
                .Append(".back-to-top{position:fixed;right:18px;bottom:18px;z-index:1001;background:#020304;color:#fff;border:1px solid var(--accent);border-radius:6px;padding:10px 13px;font-weight:1000;box-shadow:0 12px 34px rgba(0,0,0,.32)}.back-to-top:hover{background:var(--accent);color:#020304}@media(max-width:980px){.nav-wrap{align-items:flex-start;flex-direction:column;padding-top:12px;padding-bottom:14px}.nav-links{justify-content:flex-start;gap:14px}.layout,.estate-stats,.wallet-summary{grid-template-columns:1fr}.wallet-guide{display:block}.wallet-guide a{display:inline-flex;margin-top:14px}.estate-hero{min-height:500px}.hero{min-height:330px}.estate-hero h1,.hero h1,.feature-page h1,.script-reference h1,.wallet-page h1{font-size:44px}.estate-hero .wrap{padding-top:80px;padding-bottom:64px}.wrap{padding-left:18px;padding-right:18px}.script-card-head{display:block}.script-card-head span{text-align:left;display:block;margin-top:5px}.brand{min-width:0}.back-to-top{right:14px;bottom:14px;padding:9px 11px}}");
            return css.ToString();
        }

        private StringBuilder BeginPage(string title)
        {
            StringBuilder html = new StringBuilder(8192);
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<title>").Append(Html(title)).Append("</title>")
                .Append("<style>")
                .Append(RegionWebCss())
                .Append("</style></head><body id=\"top\">");
            AppendGlobalNavigation(html);
            return html;
        }

        private void AppendGlobalNavigation(StringBuilder html)
        {
            html.Append("<nav class=\"site-nav\" aria-label=\"Vanilla Sim navigation\"><div class=\"wrap nav-wrap\"><a class=\"brand\" href=\"")
                .Append(Html(m_basePath)).Append("/\"><span class=\"brand-mark\" aria-hidden=\"true\"><span></span></span><span class=\"brand-type\"><span>Vanilla</span><strong>Sim</strong></span></a><div class=\"nav-links\">")
                .Append("<a href=\"").Append(Html(m_basePath)).Append("/#regions\">Regions</a>")
                .Append("<a href=\"").Append(Html(m_basePath)).Append("/#features\">Features</a>");

            if (m_currencyPortalEnabled)
                html.Append("<a class=\"nav-cta\" href=\"").Append(Html(m_basePath)).Append("/currency/\">Wallet</a>");

            html.Append("<a class=\"nav-github\" href=\"").Append(Html(VanillaSimRepositoryUrl))
                .Append("\" target=\"_blank\" rel=\"noopener\" aria-label=\"Vanilla Sim GitHub repository\">")
                .Append("<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><path d=\"M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82A7.68 7.68 0 0 1 8 3.86c.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z\"/></svg><span>GitHub</span></a>");

            html.Append("</div></div></nav>");
        }

        private static void AppendPageLinks(StringBuilder html, params string[] labelUrlPairs)
        {
            if (labelUrlPairs == null || labelUrlPairs.Length < 2)
                return;

            html.Append("<nav class=\"page-links\" aria-label=\"Page navigation\">");
            for (int i = 0; i + 1 < labelUrlPairs.Length; i += 2)
            {
                html.Append("<a href=\"").Append(Html(labelUrlPairs[i + 1])).Append("\">")
                    .Append(Html(labelUrlPairs[i])).Append("</a>");
            }
            html.Append("</nav>");
        }

        private static string EndPage()
        {
            return "<a class=\"back-to-top\" href=\"#top\" aria-label=\"Back to top\">Top</a>"
                + "<script>(function(){var groups=document.querySelectorAll('[data-carousel]');for(var g=0;g<groups.length;g++){(function(box){var slides=box.querySelectorAll('.estate-slide');if(slides.length<2)return;var i=0;setInterval(function(){slides[i].classList.remove('is-active');i=(i+1)%slides.length;slides[i].classList.add('is-active');},9500);})(groups[g]);}})();</script>"
                + "</body></html>";
        }

        private static string Paragraphs(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string[] paragraphs = text.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder html = new StringBuilder();
            foreach (string paragraph in paragraphs)
            {
                html.Append("<p>").Append(Html(paragraph.Trim()).Replace("\n", "<br>")).Append("</p>");
            }
            return html.ToString();
        }

        private static string FirstWords(string text, int count)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string[] words = text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= count)
                return string.Join(" ", words);

            return string.Join(" ", words.Take(count).ToArray()) + "...";
        }

        private static string MakeSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "region";

            StringBuilder slug = new StringBuilder(name.Length);
            bool dash = false;
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    slug.Append(c);
                    dash = false;
                }
                else if (!dash)
                {
                    slug.Append('-');
                    dash = true;
                }
            }

            string result = slug.ToString().Trim('-');
            return string.IsNullOrEmpty(result) ? "region" : result;
        }

        private static string CleanPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "/regionweb";

            path = path.Trim();
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;

            return path.TrimEnd('/');
        }

        private static string Url(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string EscapeIni(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string FormatDate(DateTime date)
        {
            return date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        }

        private static string GetContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                default:
                    return "application/octet-stream";
            }
        }

        private static readonly ScriptFunctionDoc[] ScriptFunctionDocs = new[]
        {
            Doc("List and data fixes", "llList2ListSlice", "list llList2ListSlice(list src, integer start, integer end, integer stride, integer stride_index)", "A sliced list.", "Use it to take one entry from each stride across an inclusive range. Negative indexes and exclusion ranges now follow SL behavior.", "None.", "Corrected stride and negative-index semantics."),
            Doc("List and data fixes", "llListFindStrided", "integer llListFindStrided(list src, list test, integer start, integer end, integer stride)", "The matching list index, or -1.", "Use it to search only stride-aligned positions between start and end. Empty lists and negative ranges now match SL behavior.", "None.", "Prevents matches from leaking outside the requested search span."),
            Doc("Experience-Lite", "llRequestExperiencePermissions", "void llRequestExperiencePermissions(key agent, string experience)", "No return value; raises experience_permissions or experience_permissions_denied.", "Call before privileged Experience-Lite actions. Trusted estate owners can auto-grant configured permissions.", "Requires [ScriptExperiences] trust for automatic grants; untrusted scripts receive denied callbacks.", "The experience string may be blank for the configured local experience."),
            Doc("Experience-Lite", "llIsExperienceTrusted", "integer llIsExperienceTrusted()", "TRUE when the running object or owner is trusted.", "Use at startup to decide whether to enable estate automation features.", "None.", "Reads the simulator Experience-Lite trust configuration."),
            Doc("Experience-Lite", "llExperienceCanAutoGrant", "integer llExperienceCanAutoGrant(integer permissions)", "TRUE when every requested permission bit can be auto-granted.", "Pass the same permission mask you would request with llRequestPermissions.", "None.", "Use before asking an avatar for permissions if you want a no-prompt trusted path."),
            Doc("Experience-Lite", "llGetExperiencePermissions", "integer llGetExperiencePermissions()", "The configured auto-grant permission mask.", "Use it to inspect what the current trusted experience can request without a viewer prompt.", "None.", "PERMISSION_DEBIT and ownership-changing flows are intentionally not auto-granted by default."),
            Doc("Experience-Lite", "llAgentInExperience", "integer llAgentInExperience(key agent)", "TRUE when the agent is known to the local trusted experience.", "Use this before experience-only UI, sits or teleports.", "None.", "Returns false for unknown, offline or out-of-region agents."),
            Doc("Experience-Lite", "llGetExperienceDetails", "list llGetExperienceDetails(key experience_id)", "A list of experience metadata.", "Call with NULL_KEY for the local configured experience details.", "None.", "Includes readable status/error information for scripts."),
            Doc("Experience-Lite", "llGetExperienceErrorMessage", "string llGetExperienceErrorMessage(integer error)", "A readable error string.", "Use it in dataserver and experience denied handlers to turn XP_ERROR_* codes into owner-readable diagnostics.", "None.", "Useful for in-world setup panels."),
            Doc("Experience-Lite", "llOpenFloater", "integer llOpenFloater(string floater_name, string url, list parameters)", "A deterministic status code.", "Use it from attachment/experience workflows that compile against SL floater APIs.", "Experience trust may be required depending on the requested floater flow.", "The simulator exposes the signature and returns explicit status rather than silently doing nothing."),
            Doc("Experience-Lite", "llSitOnLink", "integer llSitOnLink(key agent, integer link)", "A SIT_* result code.", "After experience_permissions, use it to seat an avatar on a specific linked sit target.", "Requires trusted experience permissions for the target agent.", "Pairs with PRIM_SCRIPTED_SIT_ONLY and llSetLinkSitFlags."),
            Doc("Experience key-value", "llCreateKeyValue", "key llCreateKeyValue(string key, string value)", "A dataserver query id.", "Create a persistent experience key when it does not already exist.", "Requires trusted Experience-Lite storage.", "dataserver replies are \"1,value\" or \"0,errorCode\"."),
            Doc("Experience key-value", "llReadKeyValue", "key llReadKeyValue(string key)", "A dataserver query id.", "Read a persistent experience key.", "Requires trusted Experience-Lite storage.", "Use llGetExperienceErrorMessage for failed replies."),
            Doc("Experience key-value", "llUpdateKeyValue", "key llUpdateKeyValue(string key, string value, integer checked, string originalValue)", "A dataserver query id.", "Update a key. Set checked to TRUE to require the stored value to equal originalValue.", "Requires trusted Experience-Lite storage.", "Use checked updates for locks, counters and multi-script state."),
            Doc("Experience key-value", "llDeleteKeyValue", "key llDeleteKeyValue(string key)", "A dataserver query id.", "Delete a key from the local experience store.", "Requires trusted Experience-Lite storage.", "Deleting a missing key returns an error reply."),
            Doc("Experience key-value", "llKeyCountKeyValue", "key llKeyCountKeyValue()", "A dataserver query id.", "Request the number of keys in the local experience store.", "Requires trusted Experience-Lite storage.", "Useful for capacity monitors."),
            Doc("Experience key-value", "llKeysKeyValue", "key llKeysKeyValue(integer first, integer count)", "A dataserver query id.", "Page through stored keys.", "Requires trusted Experience-Lite storage.", "Respect KeyValueStoreMaxKeys and configured byte limits."),
            Doc("Experience key-value", "llDataSizeKeyValue", "key llDataSizeKeyValue()", "A dataserver query id.", "Request current byte usage for the experience key-value store.", "Requires trusted Experience-Lite storage.", "Use with llGetExperienceKeyValueStoreStats for admin panels."),
            Doc("Experience key-value", "llGetExperienceKeyValueStoreStats", "list llGetExperienceKeyValueStoreStats()", "A stats list.", "Read enabled/trusted state, key count, byte usage and configured storage limits synchronously.", "Requires trusted Experience-Lite storage for meaningful values.", "Server-local diagnostic helper."),
            Doc("Linkset data", "llLinksetDataAvailable", "integer llLinksetDataAvailable()", "Available bytes.", "Check remaining object-local linkset storage capacity.", "None.", "Scoped to the object linkset."),
            Doc("Linkset data", "llLinksetDataCountKeys", "integer llLinksetDataCountKeys()", "Number of stored keys.", "Count all linkset data keys.", "None.", "Use before paginating with llLinksetDataListKeys."),
            Doc("Linkset data", "llLinksetDataCountFound", "integer llLinksetDataCountFound(string pattern)", "Number of matching keys.", "Count keys matching a pattern.", "None.", "Pattern search mirrors the linkset data find/delete helpers."),
            Doc("Linkset data", "llLinksetDataListKeys", "list llLinksetDataListKeys(integer start, integer count)", "A list of key names.", "Page through object-local linkset data keys.", "None.", "Use count to limit chatty admin displays."),
            Doc("Linkset data", "llLinksetDataFindKeys", "list llLinksetDataFindKeys(string pattern, integer start, integer count)", "A list of matching key names.", "Search key names by pattern.", "None.", "Good for namespace-style keys such as seat:* or vendor:*." ),
            Doc("Linkset data", "llLinksetDataRead", "string llLinksetDataRead(string name)", "The stored value, or an empty string.", "Read an unprotected linkset data key.", "None.", "Protected values must be read with llLinksetDataReadProtected."),
            Doc("Linkset data", "llLinksetDataReadProtected", "string llLinksetDataReadProtected(string name, string pass)", "The stored value, or an empty string.", "Read a protected key using the pass phrase.", "None.", "Use for shared object state that should not be casually read by every script."),
            Doc("Linkset data", "llLinksetDataWrite", "integer llLinksetDataWrite(string name, string value)", "A LINKSETDATA_* result code.", "Write or replace an unprotected object-local key.", "None.", "Triggers linkset_data in scripts in the same object."),
            Doc("Linkset data", "llLinksetDataWriteProtected", "integer llLinksetDataWriteProtected(string name, string value, string pass)", "A LINKSETDATA_* result code.", "Write or replace a protected key.", "None.", "The same pass phrase is required for protected read/delete."),
            Doc("Linkset data", "llLinksetDataDelete", "integer llLinksetDataDelete(string name)", "A LINKSETDATA_* result code.", "Delete an unprotected key.", "None.", "Triggers linkset_data when a value is removed."),
            Doc("Linkset data", "llLinksetDataDeleteProtected", "integer llLinksetDataDeleteProtected(string name, string pass)", "A LINKSETDATA_* result code.", "Delete a protected key.", "None.", "Requires the matching pass phrase."),
            Doc("Linkset data", "llLinksetDataDeleteFound", "list llLinksetDataDeleteFound(string pattern, string pass)", "A list of deleted keys.", "Delete all matching keys, optionally using a pass phrase for protected keys.", "None.", "Use carefully in admin reset scripts."),
            Doc("Linkset data", "llLinksetDataReset", "void llLinksetDataReset()", "No return value.", "Clear all linkset data for the object.", "None.", "Best reserved for owner/admin reset tools."),
            Doc("Scripted sit", "llSetLinkSitFlags", "void llSetLinkSitFlags(integer link, integer flags)", "No return value.", "Set SIT_FLAG_* behavior on a link, including scripted-only sit and allow-unsit control.", "Object owner/control script.", "Use PRIM_SCRIPTED_SIT_ONLY and PRIM_ALLOW_UNSIT for viewer-compatible seats."),
            Doc("Scripted sit", "llGetLinkSitFlags", "integer llGetLinkSitFlags(integer link)", "The SIT_FLAG_* bitmask.", "Read the scripted sit flags for a link.", "None.", "Use in setup validators."),
            Doc("Rez and cleanup", "llRezObjectWithParams", "key llRezObjectWithParams(string inventory, list params)", "The rezzed object key, or NULL_KEY on failure.", "Rez an inventory object using REZ_* parameters for position, rotation, velocity, start data and flags.", "Requires normal rez rights and inventory permissions.", "Use llGetStartString inside the rezzed object for string start data."),
            Doc("Rez and cleanup", "llDerezObject", "integer llDerezObject(key object_id, integer flag)", "A derez status code.", "Remove a scripted object by id using the supported DEREZ/return behavior.", "Requires object ownership or sufficient estate return rights.", "Useful for temporary build and vehicle cleanup."),
            Doc("Rez and cleanup", "llGetStartString", "string llGetStartString()", "The string start parameter.", "Read string start data supplied by llRezObjectWithParams.", "None.", "This was already in the API and is now exposed through the stub."),
            Doc("Linked sound", "llLinkPlaySound", "void llLinkPlaySound(integer link, string sound, float volume[, integer flags])", "No return value.", "Play a sound from a specific linked prim, optionally using SOUND_* flags.", "The sound must be an object inventory item or asset id the simulator can resolve.", "Use link selectors for multi-prim vehicles and machines."),
            Doc("Linked sound", "llLinkStopSound", "void llLinkStopSound(integer link)", "No return value.", "Stop sound on the selected link.", "None.", "Pairs with llLinkPlaySound."),
            Doc("Linked sound", "llLinkAdjustSoundVolume", "void llLinkAdjustSoundVolume(integer link, float volume)", "No return value.", "Adjust volume on a playing linked sound.", "None.", "Volume follows the normal 0.0 to 1.0 range."),
            Doc("Linked sound", "llLinkSetSoundQueueing", "void llLinkSetSoundQueueing(integer link, integer queue)", "No return value.", "Enable or disable queued sound behavior on a link.", "None.", "Use before a sequence of linked sound calls."),
            Doc("Linked sound", "llLinkSetSoundRadius", "void llLinkSetSoundRadius(integer link, float radius)", "No return value.", "Set audible radius for a linked sound emitter.", "None.", "Good for local machine sounds that should not fill the whole region."),
            Doc("Environment and time", "llGetRegionTimeOfDay", "float llGetRegionTimeOfDay()", "Seconds into the current region day.", "Read EEP region time when the environment module is available.", "None.", "Falls back to llGetTimeOfDay when no region environment module exists."),
            Doc("Environment and time", "llGetDayLength", "integer llGetDayLength()", "Current parcel/day length in seconds.", "Use for scripts that sync lighting, games or machines to the local day cycle.", "None.", "Alias-style helper for the active environment."),
            Doc("Environment and time", "llGetRegionDayLength", "integer llGetRegionDayLength()", "Region day length in seconds.", "Use when you need the region cycle rather than parcel/agent local values.", "None.", "Reads the region environment settings."),
            Doc("Environment and time", "llGetDayOffset", "integer llGetDayOffset()", "Day offset in seconds.", "Read the current environment offset.", "None.", "Use with day length to align scripted effects."),
            Doc("Environment and time", "llGetRegionDayOffset", "integer llGetRegionDayOffset()", "Region day offset in seconds.", "Read the region-level day offset.", "None.", "Region-scoped counterpart to llGetDayOffset."),
            Doc("Environment and time", "llGetSunDirection", "vector llGetSunDirection()", "A direction vector.", "Aim lights, panels or sundials at the current sun direction.", "None.", "Uses the active environment."),
            Doc("Environment and time", "llGetRegionSunDirection", "vector llGetRegionSunDirection()", "A direction vector.", "Aim scripts at the region sun direction.", "None.", "Region-scoped counterpart to llGetSunDirection."),
            Doc("Environment and time", "llGetMoonDirection", "vector llGetMoonDirection()", "A direction vector.", "Aim scripts at the current moon direction.", "None.", "Uses the active environment."),
            Doc("Environment and time", "llGetRegionMoonDirection", "vector llGetRegionMoonDirection()", "A direction vector.", "Aim scripts at the region moon direction.", "None.", "Region-scoped counterpart to llGetMoonDirection."),
            Doc("Environment and time", "llGetSunRotation", "rotation llGetSunRotation()", "A rotation.", "Use when a script needs the current sun orientation as a rotation.", "None.", "Uses the active environment."),
            Doc("Environment and time", "llGetRegionSunRotation", "rotation llGetRegionSunRotation()", "A rotation.", "Use when a script needs the region sun orientation.", "None.", "Region-scoped counterpart to llGetSunRotation."),
            Doc("Environment and time", "llGetMoonRotation", "rotation llGetMoonRotation()", "A rotation.", "Use when a script needs the current moon orientation as a rotation.", "None.", "Uses the active environment."),
            Doc("Environment and time", "llGetRegionMoonRotation", "rotation llGetRegionMoonRotation()", "A rotation.", "Use when a script needs the region moon orientation.", "None.", "Region-scoped counterpart to llGetMoonRotation."),
            Doc("Environment and time", "llGetEnvironment", "list llGetEnvironment(vector position, list rules)", "Values matching the requested rules.", "Query supported EEP day, sky, water and environment rules at a position.", "None.", "Includes supported SKY_* and WATER_* readback such as ambient, blue, haze, sun, moon, clouds, texture ids, fog, fresnel, waves, normal scale, normal texture and refraction."),
            Doc("Environment and time", "llReplaceEnvironment", "integer llReplaceEnvironment(vector position, string environment, integer track_no, integer day_length, integer day_offset)", "An ENV_* result code.", "Replace or clear parcel/region environment data using an inventory/environment asset id.", "Requires parcel or estate environment rights.", "Pass NULL_KEY or an empty string to clear where supported."),
            Doc("Environment and time", "llSetEnvironment", "integer llSetEnvironment(vector position, list parameters)", "An ENV_* result code.", "Set supported per-parameter environment overrides at a parcel position or for the whole region when x/y are negative.", "Requires parcel or estate environment rights.", "Supports persistent ENVIRONMENT_DAYINFO, SKY_TRACKS, SKY_AMBIENT, SKY_BLUE, SKY_CLOUDS, SKY_DOME, SKY_GAMMA, SKY_GLOW, SKY_HAZE, SKY_MOON, SKY_PLANET, SKY_REFRACTION, SKY_REFLECTION_PROBE_AMBIANCE, SKY_STAR_BRIGHTNESS, SKY_SUN and sky texture ids, plus WATER_* blur, fog, fresnel, normal scale, refraction, waves and normal texture."),
            Doc("Environment and time", "llReplaceAgentEnvironment", "integer llReplaceAgentEnvironment(key agent, float transition, string environment)", "An ENV_* result code.", "Replace or clear a local agent environment.", "Requires a valid in-region agent and supported environment permissions.", "Useful for trusted estate experiences and ride effects."),
            Doc("Environment and time", "llSetAgentEnvironment", "integer llSetAgentEnvironment(key agent, float transition, list parameters)", "An ENV_* result code.", "Set supported per-agent environment sky and water parameters for the experience-permission avatar.", "Requires a valid in-region agent and supported environment permissions.", "Uses the same supported SKY_* and WATER_* parameter subset as llSetEnvironment for local viewer environment effects."),
            Doc("Estate and parcel", "llReturnObjectsByID", "integer llReturnObjectsByID(list object_ids)", "Number of objects returned.", "Return selected objects by UUID.", "Requires PERMISSION_RETURN_OBJECTS or simulator return rights.", "Uses normal simulator permission checks."),
            Doc("Estate and parcel", "llReturnObjectsByOwner", "integer llReturnObjectsByOwner(key owner, integer scope)", "Number of objects returned.", "Return objects owned by an avatar within the selected OBJECT_RETURN_* scope.", "Requires PERMISSION_RETURN_OBJECTS or simulator return rights.", "Use for estate cleanup panels."),
            Doc("Estate and parcel", "llSetGroundTexture", "integer llSetGroundTexture(list changes)", "TRUE on success.", "Set TERRAIN_DETAIL_* textures and TERRAIN_HEIGHT_RANGE_* blending heights.", "Script owner must be estate owner or estate manager.", "Estate manager checks now use the same owner-or-manager path as estate commands."),
            Doc("Estate and parcel", "llSetParcelForSale", "integer llSetParcelForSale(integer forSale, list options)", "A PARCEL_SALE_* result code.", "Mark the current parcel for sale or clear sale state using sale options.", "Requires parcel ownership or PERMISSION_PRIVILEGED_LAND_ACCESS where supported.", "Use for scripted land consoles."),
            Doc("Estate and parcel", "llGetParcelPrimCount", "integer llGetParcelPrimCount(vector pos, integer category, integer sim_wide)", "Prim count for the requested PARCEL_COUNT_* category.", "Read parcel-local or same-owner simulator-wide counts for total, owner, group, other, selected and temporary prims.", "None.", "With sim_wide TRUE, counts are summed across parcels in the region with the same land owner as the parcel at pos. PARCEL_COUNT_TEMP follows the SL caveat that temporary mesh linksets are not counted."),
            Doc("Estate and parcel", "llGetParcelDetails", "list llGetParcelDetails(vector pos, list params)", "Values matching the requested PARCEL_DETAILS_* constants.", "Read parcel identity, owner, group, area, visibility, landing, routing, flags, script danger and prim capacity/usage details.", "None.", "PARCEL_DETAILS_PRIM_CAPACITY and PARCEL_DETAILS_PRIM_USED follow Second Life's same-owner simulator-wide behavior, not just the single parcel under pos."),
            Doc("Estate and parcel", "llGetParcelPrimOwners", "list llGetParcelPrimOwners(vector pos)", "Owner key/count pairs.", "Audit which owners are consuming prim count on the parcel under pos.", "None.", "Pairs with llGetParcelPrimCount and llGetParcelDetails for rental and estate rule consoles."),
            Doc("Estate and parcel", "llParcelMediaCommandList", "void llParcelMediaCommandList(list commands)", "No return value.", "Set parcel media URL, texture, loop, auto-align, MIME type, description and size commands.", "Requires parcel media edit rights.", "PARCEL_MEDIA_COMMAND_LOOP_SET is supported."),
            Doc("Estate and parcel", "llParcelMediaQuery", "list llParcelMediaQuery(list commands)", "Requested media values.", "Read parcel media state for supported query fields.", "Requires parcel media visibility/edit context.", "Returns values in the requested command order."),
            Doc("Estate and parcel", "llManageEstateAccess", "integer llManageEstateAccess(integer action, string avatar)", "TRUE on successful mutation.", "Change estate access lists from trusted estate scripts.", "Script owner must be estate owner or estate manager.", "Mutations persist and notify estate info updates."),
            Doc("Inventory and ownership", "llGiveAgentInventory", "integer llGiveAgentInventory(key agent, string folderName, list inventory, list options)", "A TRANSFER_* result code.", "Deliver a folder of task inventory to an in-region agent.", "Items must satisfy copy/transfer checks.", "Use TRANSFER_DEST and TRANSFER_FLAGS options."),
            Doc("Inventory and ownership", "llTransferOwnership", "integer llTransferOwnership(key agent, integer flags, list options)", "A TRANSFER_* result code.", "Transfer the object or copy/take inventory delivery to another agent.", "Requires compatible object and inventory permissions.", "TRANSFER_FLAG_COPY and TRANSFER_FLAG_TAKE are supported."),
            Doc("Inventory and ownership", "llGiveMoney", "integer llGiveMoney(key destination, integer amount)", "TRUE when the economy backend accepts the transfer, otherwise FALSE.", "Send money from the object owner to an avatar using the configured money module.", "Requires owner-granted PERMISSION_DEBIT, a positive amount, a non-group-owned object and an avatar target.", "Rejects debit grants from non-owners and object UUID targets before calling the money backend."),
            Doc("Inventory and ownership", "llTransferLindenDollars", "key llTransferLindenDollars(key destination, integer amount)", "A transaction/query id.", "Start a scripted money transfer where the economy backend supports it.", "Requires owner-granted PERMISSION_DEBIT, a positive amount, a non-group-owned object, an avatar target and economy support.", "Reports success/failure through transaction_result."),
            Doc("Inventory and ownership", "llGetInventoryAcquireTime", "string llGetInventoryAcquireTime(string item)", "Acquire timestamp text.", "Read when an inventory item was acquired by the object.", "None.", "Returns an error if the item does not exist."),
            Doc("Inventory and ownership", "llGetInventoryDesc", "string llGetInventoryDesc(string item)", "Inventory item description.", "Read the description field for an object inventory item.", "None.", "Useful for data-driven object inventory."),
            Doc("Avatar and detection", "llDetectedRezzer", "key llDetectedRezzer(integer number)", "The rezzer object/avatar key, or NULL_KEY.", "Read provenance from detected data after sensor/collision/touch-style callbacks.", "None.", "The rezzer id now survives YEngine capture and restore."),
            Doc("Avatar and detection", "llGetAttachedListFiltered", "list llGetAttachedListFiltered(key agent, list options)", "Attachment object ids.", "Query attachments with FILTER_* options such as ATTACH_ANY_HUD and FILTER_FLAG_HUDS.", "HUD attachment visibility is limited to the script owner.", "Use for HUD-aware controllers without manual relay scripts."),
            Doc("Avatar and detection", "llSetAgentRot", "void llSetAgentRot(rotation rot, integer flags)", "No return value.", "Apply yaw rotation to the permissions-granted in-region avatar.", "Requires animation/control permissions for the avatar.", "Only yaw rotation is applied."),
            Doc("Avatar and detection", "llGetAnimation", "string llGetAnimation(key agent)", "The avatar's current SL-style animation state, or an empty string.", "Read high-level avatar movement state for seats, HUDs, region games and visitor diagnostics.", "The avatar must be a root agent in the same region.", "Reports Sitting and Sitting on Ground from simulator sit state even before movement animation heartbeat catches up."),
            Doc("Avatar and detection", "llGetAnimationList", "list llGetAnimationList(key agent)", "A list of active animation UUIDs.", "Inspect the active animation stack for an in-region avatar.", "The avatar must be a root agent in the same region.", "Use with llGetAnimation when you need both friendly state names and raw animation ids."),
            Doc("Avatar and detection", "llWorldPosToHUD", "vector llWorldPosToHUD(vector world_position)", "A HUD-space coordinate.", "Convert a world position to a HUD coordinate for indicators and pointing UI.", "Works from an attached HUD context.", "Useful for minimaps, markers and targeting displays."),
            Doc("Avatar and detection", "llMatchGroup", "integer llMatchGroup(key agent, list group_keys)", "TRUE when the agent active group matches.", "Check whether an in-region avatar has one of the supplied active groups.", "None.", "Avoids needing scripted llSameGroup relay prims."),
            Doc("Avatar and detection", "llIsFriend", "integer llIsFriend(key agent)", "TRUE when the simulator can treat the agent as a friend/same group.", "Use for compatibility with scripts that check friend-like access.", "None.", "Falls back to same-group behavior when friend service state is unavailable."),
            Doc("Avatar and detection", "llGetAgentLanguage", "string llGetAgentLanguage(key agent)", "The avatar language tag, or an empty string.", "Use it for local greeting, translation routing and accessibility panels.", "The avatar must be in the same region and have public language sharing enabled.", "Returns empty for child agents, offline/remote agents, hidden language preferences or missing agent-preference service."),
            Doc("Avatar and detection", "llGetVisualParams", "list llGetVisualParams(key agent, list visual_params)", "One value per requested visual parameter, or an empty string for unsupported/unavailable entries.", "Read supported Second Life avatar visual parameters by numeric id or common name, including height, torso_length, male, heel_height, platform_height, shoe_height, hand_size, head_size, leg_length, arm_length, neck_length, waist_height, hip_length and hover.", "The avatar must be known to the region and have appearance data available.", "Accepts case-insensitive names and common aliases, returns normalized float values from 0.0 to 1.0, and safely handles legacy appearance arrays without script-engine exceptions."),
            Doc("Identity lookup", "llKey2Name", "string llKey2Name(key id)", "Avatar/object name or an empty string.", "Resolve in-region avatars and objects, plus cached local user accounts.", "None.", "Useful for inspector HUDs and owner consoles that need stable names after an avatar leaves the region."),
            Doc("Identity lookup", "llGetUsername", "string llGetUsername(key id)", "Lowercase username or an empty string.", "Resolve usernames for live avatars and cached local user accounts.", "None.", "Converts legacy names to SL-style usernames such as firstname.lastname or firstname for Resident accounts."),
            Doc("Identity lookup", "llGetDisplayName", "string llGetDisplayName(key id)", "Display name or an empty string.", "Resolve display/legacy names for live avatars and cached local user accounts.", "None.", "OpenSim currently returns the account name as the display-name-compatible value."),
            Doc("Identity lookup", "llName2Key", "key llName2Key(string name)", "Avatar key or NULL_KEY.", "Resolve a live or cached local account name synchronously.", "None.", "Use llRequestUserKey for async scripts and Hypergrid lookups."),
            Doc("Identity lookup", "llRequestUsername", "key llRequestUsername(key id)", "Dataserver query id.", "Request a username for a key and receive it in dataserver.", "None.", "Uses immediate replies for live avatars and cached/account lookup otherwise."),
            Doc("Identity lookup", "llRequestDisplayName", "key llRequestDisplayName(key id)", "Dataserver query id.", "Request a display name for a key and receive it in dataserver.", "None.", "OpenSim currently returns the account name as the display-name-compatible value."),
            Doc("Identity lookup", "llRequestUserKey", "key llRequestUserKey(string username)", "Dataserver query id.", "Request an avatar key by name or username.", "None.", "Resolves live users, local cached accounts and supported Hypergrid names where the backend can query them."),
            Doc("Script diagnostics", "llGetFreeMemory", "integer llGetFreeMemory()", "Free script heap bytes.", "Use it before allocating large lists or strings.", "None.", "YEngine returns the real remaining tracked heap for the running script."),
            Doc("Script diagnostics", "llGetUsedMemory", "integer llGetUsedMemory()", "Used script heap bytes.", "Use it to watch object-local data structures grow and shrink.", "None.", "YEngine returns the tracked heap currently used by globals, arrays and locals."),
            Doc("Script diagnostics", "llGetMemoryLimit", "integer llGetMemoryLimit()", "Current script heap limit in bytes.", "Use it with llGetFreeMemory and llGetUsedMemory for memory panels and self-tests.", "None.", "YEngine returns the active per-script heap limit instead of a static placeholder."),
            Doc("Script diagnostics", "llSetMemoryLimit", "integer llSetMemoryLimit(integer limit)", "TRUE when the requested limit is accepted.", "Call it to lower or restore the active heap limit for the running script.", "The limit must be at least 16384 bytes, not exceed llGetSPMaxMemory and not be below current used memory.", "YEngine enforces the new limit for subsequent heap allocations."),
            Doc("Script diagnostics", "llGetSPMaxMemory", "integer llGetSPMaxMemory()", "Maximum script heap bytes available from the current engine configuration.", "Use it before raising a limit or reporting simulator capacity.", "None.", "Returns the configured YEngine script heap maximum for the instance."),
            Doc("Script diagnostics", "llScriptProfiler", "void llScriptProfiler(integer flags)", "No return value.", "Enable or disable script profiler flags such as PROFILE_SCRIPT_MEMORY or PROFILE_NONE for compatibility diagnostics.", "None.", "YEngine records profiler flags, heap usage, event count, slice count and CPU milliseconds in prim dynamic attributes for viewer-adjacent tools and Vanilla Sim web inspection."),
            Doc("Object inspection", "llGetObjectDetails", "list llGetObjectDetails(key id, list params)", "Values matching the requested OBJECT_* constants.", "Use OBJECT_SERVER_COST, OBJECT_STREAMING_COST, OBJECT_PHYSICS_COST, OBJECT_PRIM_EQUIVALENCE, OBJECT_RENDER_WEIGHT, OBJECT_HOVER_HEIGHT and OBJECT_SELECT_COUNT to build SL-style diagnostics panels.", "None for visible in-region objects or avatars.", "This build returns linkset-level object cost estimates, avatar attachment cost estimates, avatar hover height and selected-linkset state instead of placeholder zeroes for those details."),
            Doc("Physics and movement", "llGetEnergy", "float llGetEnergy()", "A 0.0 to 1.0 script energy value.", "Check available object energy before repeated physical pushes, impulses, hover, buoyancy or look-at control.", "None.", "Energy is tracked per linkset, drains on supported physical-control calls and recharges over time; it is readback-compatible and does not clamp physics calls."),
            Doc("Materials and rendering", "llSetRenderMaterial", "void llSetRenderMaterial(string material, integer face)", "No return value.", "Apply a render material inventory item or material id to one face on the current prim.", "The material must resolve from object inventory or a valid asset id.", "Use an empty string to clear where supported."),
            Doc("Materials and rendering", "llSetLinkRenderMaterial", "void llSetLinkRenderMaterial(integer link, string material, integer face)", "No return value.", "Apply a render material to selected linked prims/faces.", "The material must resolve from object inventory or a valid asset id.", "For inventory names, the material item must be inside the object."),
            Doc("Materials and rendering", "llGetRenderMaterial", "string llGetRenderMaterial(integer face)", "The stored material id/name, or an empty string.", "Read the render material assigned to a face.", "None.", "Use PRIM_GLTF_* primitive params to inspect supported material properties."),
            Doc("Materials and rendering", "llSetLinkGLTFOverrides", "void llSetLinkGLTFOverrides(integer link, integer face, list overrides)", "No return value.", "Set supported OVERRIDE_GLTF_* factors on selected linked prims/faces.", "Object edit rights.", "Supports base color/alpha, alpha mode, mask cutoff, double-sided, metallic, roughness and emissive factors; readback merges assigned material asset values with stored overrides. OVERRIDE_GLTF_EXTENSION_JSON stores extension JSON for future GLTF-compatible tooling."),
            Doc("Materials and rendering", "llIsLinkGLTFMaterial", "integer llIsLinkGLTFMaterial(integer link, integer face)", "TRUE when a face has GLTF material data.", "Check before applying or reading GLTF-specific overrides.", "None.", "Useful for mixed legacy/PBR builds; PRIM_GLTF_* readback now includes supported assigned material asset values when present."),
            Doc("Damage and combat", "llSetDamage", "void llSetDamage(float damage)", "No return value.", "Set object collision damage value.", "Object script control.", "Also available through PRIM_DAMAGE primitive params."),
            Doc("Damage and combat", "llDamage", "void llDamage(key target, float damage, integer damage_type)", "No return value.", "Apply supported avatar/object health damage and damage type.", "Requires damage-enabled land for avatars.", "Creates a pre-health damage transaction, posts on_damage, waits for quiet llAdjustDamage() changes, then applies health and posts final_damage/on_death."),
            Doc("Damage and combat", "llGetHealth", "float llGetHealth(string key)", "The target health value when known.", "Read avatar/object health compatibility state.", "None.", "Works for avatars and PRIM_HEALTH-enabled objects."),
            Doc("Damage and combat", "llDetectedDamage", "list llDetectedDamage(integer number)", "A list [damage, damage_type, original_damage, source_key, source_position, source_owner].", "Inspect Combat2-style damage metadata inside on_damage and final_damage.", "Only meaningful while processing damage events.", "Damage metadata survives YEngine event capture/restore."),
            Doc("Damage and combat", "llAdjustDamage", "void llAdjustDamage(integer number, float damage)", "No return value.", "Adjust the current event's damage metadata for Combat2-style scripts.", "Only meaningful while processing on_damage/final_damage metadata.", "When called from on_damage, the adjusted value extends the transaction quiet window and is used before health is reduced; a one-argument compatibility overload adjusts detected row 0."),
            Doc("Security", "llComputeHash", "string llComputeHash(string message, string algorithm)", "Hex digest text.", "Hash data using supported algorithm names for web callbacks or signatures.", "None.", "Use the exact algorithm names supported by the runtime."),
            Doc("Security", "llHMAC", "string llHMAC(string private_key, string message, string algorithm)", "Hex HMAC text.", "Authenticate messages with a shared secret.", "None.", "Good for script-to-web handshakes."),
            Doc("Security", "llSignRSA", "string llSignRSA(string private_key, string message, string algorithm)", "Base64 RSA signature.", "Sign a message using a PEM RSA private key.", "The key must be available to the script as text.", "Supports SHA-1, SHA-224, SHA-256, SHA-384 and SHA-512 names."),
            Doc("Security", "llVerifyRSA", "integer llVerifyRSA(string public_key, string message, string signature, string algorithm)", "TRUE when the signature verifies.", "Verify an RSA signature using a PEM public key.", "None.", "Use to validate signed notecards, webhooks or configuration blobs."),
            Doc("Text, JSON and color", "llFindNotecardTextSync", "list llFindNotecardTextSync(string name, string pattern, integer start, integer count, list options)", "A list of [line, index, length] strides.", "Search a cached notecard synchronously with a regex pattern.", "The notecard must be in object inventory.", "Returns up to 64 matches per call."),
            Doc("Text, JSON and color", "llGetNotecardLineSync", "string llGetNotecardLineSync(string name, integer line)", "The notecard line text.", "Read a cached notecard line synchronously.", "The notecard must be in object inventory.", "Use async llGetNotecardLine for large or uncached data flows."),
            Doc("Text, JSON and color", "llJson2List", "list llJson2List(string json)", "A list representation.", "Convert JSON arrays/objects into LSL list form.", "None.", "Pairs with llList2Json."),
            Doc("Text, JSON and color", "llList2Json", "string llList2Json(string type, list values)", "JSON text.", "Build a JSON array or object from LSL values.", "None.", "Use JSON_ARRAY or JSON_OBJECT style type constants."),
            Doc("Text, JSON and color", "llJsonGetValue", "string llJsonGetValue(string json, list specifiers)", "The selected JSON value.", "Read a JSON path using LSL specifiers.", "None.", "Returns JSON_INVALID when the path cannot be resolved."),
            Doc("Text, JSON and color", "llJsonSetValue", "string llJsonSetValue(string json, list specifiers, string value)", "Updated JSON text.", "Set or replace a JSON value at the given path.", "None.", "Good for compact config blobs in linkset data."),
            Doc("Text, JSON and color", "llJsonValueType", "string llJsonValueType(string json, list specifiers)", "A JSON type string.", "Inspect the type at a JSON path.", "None.", "Use before reading optional keys."),
            Doc("Text, JSON and color", "llChar", "string llChar(integer unicode)", "A one-character string.", "Build a character from a Unicode code point.", "None.", "Compatibility helper for scripts ported from SL."),
            Doc("Text, JSON and color", "llOrd", "integer llOrd(string text, integer index)", "Unicode code point.", "Read the code point at an index.", "None.", "Negative indexes are not used; validate before calling."),
            Doc("Text, JSON and color", "llHash", "integer llHash(string text)", "A deterministic integer hash.", "Hash a string into an integer for buckets or lightweight ids.", "None.", "Not a cryptographic hash; use llComputeHash for security."),
            Doc("Text, JSON and color", "llReplaceSubString", "string llReplaceSubString(string src, string pattern, string replacement, integer count)", "Updated string.", "Replace regex pattern matches in a string.", "None.", "The regex is time-limited to protect the script thread."),
            Doc("Text, JSON and color", "llLinear2sRGB", "vector llLinear2sRGB(vector color)", "sRGB color vector.", "Convert linear color values to sRGB.", "None.", "Useful for PBR color workflows."),
            Doc("Text, JSON and color", "llsRGB2Linear", "vector llsRGB2Linear(vector color)", "Linear color vector.", "Convert sRGB color values to linear space.", "None.", "Useful before GLTF factor math."),
            Doc("Pathfinding compatibility", "llCreateCharacter", "void llCreateCharacter(list options)", "No return value; posts path_update success.", "Initialize the local pathfinding character backend and persist CHARACTER_* options.", "Linden's proprietary navmesh service is not present.", "Stores speed, radius, length, avoidance mode and parcel-stay settings for subsequent route calls; route generation uses the cached terrain navmesh plus dynamic obstacle overlays."),
            Doc("Pathfinding compatibility", "llUpdateCharacter", "void llUpdateCharacter(list options)", "No return value; posts path_update success.", "Update persisted character options for SL-script compatibility.", "Linden's proprietary navmesh service is not present.", "Updated options are consumed by subsequent cached-navmesh obstacle-aware movement calls."),
            Doc("Pathfinding compatibility", "llDeleteCharacter", "void llDeleteCharacter()", "No return value.", "Stop character motion and remove persisted local character state.", "None.", "Invalidates pending path completion events for the old movement."),
            Doc("Pathfinding compatibility", "llExecCharacterCmd", "void llExecCharacterCmd(integer command, list options)", "No return value; posts path_update.", "Stop, smooth-stop or jump a local pathfinding character.", "Object must not be an attachment or physical object.", "Stop commands invalidate pending completion events; CHARACTER_CMD_JUMP moves the object up locally when supported."),
            Doc("Pathfinding compatibility", "llNavigateTo", "void llNavigateTo(vector goal, list options)", "No return value; posts path_update.", "Move the scripted object along an A* route over cached terrain while avoiding scene-object bounds and optional avatar clearance.", "Object must not be an attachment or physical object.", "Honors FORCE_DIRECT_PATH, REQUIRE_LINE_OF_SIGHT, CHARACTER_STAY_WITHIN_PARCEL and posts PU_GOAL_REACHED only after keyframed movement finishes."),
            Doc("Pathfinding compatibility", "llWanderWithin", "void llWanderWithin(vector origin, vector distance, list options)", "No return value; posts path_update.", "Pick a random target inside the requested rectangle and route to it.", "Object must not be an attachment or physical object.", "Uses the same terrain/object/avatar obstacle backend as llNavigateTo."),
            Doc("Pathfinding compatibility", "llPursue", "void llPursue(key target, list options)", "No return value; posts path_update.", "Route toward an avatar/object target.", "Target must be known in the region.", "PURSUIT_OFFSET is honored before route generation."),
            Doc("Pathfinding compatibility", "llEvade", "void llEvade(key target, list options)", "No return value; posts path_update.", "Route away from an avatar/object target.", "Target must be known in the region.", "Uses the same local backend as llFleeFrom."),
            Doc("Pathfinding compatibility", "llFleeFrom", "void llFleeFrom(vector source, float distance, list options)", "No return value; posts path_update.", "Route away from a source point.", "Object must not be an attachment or physical object.", "The generated route is clamped inside the current region and remains terrain-aware and obstacle-aware."),
            Doc("Pathfinding compatibility", "llGetStaticPath", "list llGetStaticPath(vector start, vector end, float radius, list parameters)", "A list [PU_GOAL_REACHED, waypoint...] or a PU_FAILURE_* code.", "Query the local cached terrain/static-obstacle path between two points.", "Linden's proprietary navmesh service is not present.", "Returns invalid-start/goal/unreachable failures and includes simplified waypoints on success; persisted character parcel/avoidance options influence the route."),
            Doc("Pathfinding compatibility", "llGetClosestNavPoint", "vector llGetClosestNavPoint(vector point, list options)", "The nearest terrain/object-clear point in the current region or ZERO_VECTOR.", "Conform a point to region bounds, terrain height and static obstacle clearance.", "None.", "Uses GCNP_RADIUS/CHARACTER_RADIUS as the minimum clearance above terrain/objects."),
            Doc("Misc compatibility", "llGenerateKey", "key llGenerateKey()", "A generated UUID.", "Generate a random UUID from script.", "None.", "Useful for local correlation ids."),
            Doc("Misc compatibility", "llGetAgentList", "list llGetAgentList(integer scope, list options)", "Agent keys.", "List agents matching scope/options.", "None.", "Use for region HUDs and access panels."),
            Doc("Misc compatibility", "llGetObjectLinkKey", "key llGetObjectLinkKey(key object_id, integer link)", "The child prim key.", "Resolve a link key on another object where visible to the simulator.", "None.", "Useful for object inspectors."),
            Doc("Misc compatibility", "llGetCameraAspect", "float llGetCameraAspect()", "Viewer camera aspect ratio.", "Read camera aspect after camera tracking permission.", "Requires PERMISSION_TRACK_CAMERA.", "Returns an error without permission."),
            Doc("Misc compatibility", "llGetCameraFOV", "float llGetCameraFOV()", "Viewer camera field of view.", "Read camera FOV after camera tracking permission.", "Requires PERMISSION_TRACK_CAMERA.", "Returns an error without permission."),
            Doc("Misc compatibility", "llSetAnimationOverride", "void llSetAnimationOverride(string anim_state, string animation)", "No return value.", "Set animation override state for the permissions-granted avatar.", "Requires animation override permission.", "Inventory animation names must resolve."),
            Doc("Misc compatibility", "llResetAnimationOverride", "void llResetAnimationOverride(string anim_state)", "No return value.", "Reset one animation override state.", "Requires animation override permission.", "Use an empty state to clear supported sets where accepted."),
            Doc("Misc compatibility", "llGetAnimationOverride", "string llGetAnimationOverride(string anim_state)", "Animation name or empty string.", "Read the active override for a state.", "Requires animation override permission.", "Useful in AO setup scripts."),
            Doc("Misc compatibility", "llSetSculptAnim", "void llSetSculptAnim(integer mode, integer sizex, integer sizey, integer start_frame, integer end_frame, float rate, integer texture_sync)", "No return value.", "Store SL sculpt-map animation parameters on the prim and mirror them through texture animation for viewer-visible playback.", "None.", "The exact sculpt animation request persists in dynamic attributes; the visible transport uses the standard texture animation packet because OpenSim has no separate sculpt animation field.")
        };

        private const string AutoScriptFunctionCategory = "Auto-discovered API surface";
        private static readonly System.Lazy<ScriptFunctionDoc[]> CompleteScriptFunctionDocs = new System.Lazy<ScriptFunctionDoc[]>(BuildCompleteScriptFunctionDocs);

        private static ScriptFunctionDoc[] GetScriptFunctionDocs()
        {
            return CompleteScriptFunctionDocs.Value;
        }

        private static ScriptFunctionDoc[] BuildCompleteScriptFunctionDocs()
        {
            List<ScriptFunctionDoc> docs = new List<ScriptFunctionDoc>(ScriptFunctionDocs);
            HashSet<string> documented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ApiInterfaceDocSet apiDocs = LoadApiInterfaceDocs();

            foreach (ScriptFunctionDoc doc in ScriptFunctionDocs)
                documented.Add(doc.Name);

            foreach (MethodInfo method in typeof(ILSL_Api).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!method.Name.StartsWith("ll", StringComparison.Ordinal) || documented.Contains(method.Name))
                    continue;

                string apiDescription;
                bool hasApiDescription = apiDocs.Descriptions.TryGetValue(method.Name, out apiDescription)
                    && !string.IsNullOrWhiteSpace(apiDescription);
                string signature;
                if (!apiDocs.Signatures.TryGetValue(method.Name, out signature))
                    signature = FormatScriptFunctionSignature(method);
                string returnValue;
                if (!apiDocs.ReturnValues.TryGetValue(method.Name, out returnValue))
                    returnValue = FormatScriptFunctionReturn(method);
                string usage = hasApiDescription
                    ? apiDescription
                    : "Auto-discovered from the public ILSL_Api surface so the Vanilla Sim reference stays complete when new LSL functions are exposed.";
                string notes = hasApiDescription
                    ? "Description imported from the //ApiDesc comment beside the ILSL_Api declaration; add a hand-written Vanilla Sim entry when this function receives compatibility-specific behavior, examples or caveats."
                    : "Add a hand-written Vanilla Sim entry when this function receives compatibility-specific behavior, examples or caveats.";

                docs.Add(Doc(
                    AutoScriptFunctionCategory,
                    method.Name,
                    signature,
                    returnValue,
                    usage,
                    "See the simulator implementation and normal LSL permission rules for runtime restrictions.",
                    notes));
                documented.Add(method.Name);
            }

            return docs
                .OrderBy(doc => doc.Category == AutoScriptFunctionCategory ? 1 : 0)
                .ThenBy(doc => doc.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(doc => doc.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static ApiInterfaceDocSet LoadApiInterfaceDocs()
        {
            ApiInterfaceDocSet docs = new ApiInterfaceDocSet();
            string sourcePath = FindApiInterfaceSourcePath();
            if (string.IsNullOrEmpty(sourcePath))
                return docs;

            try
            {
                string pendingDescription = null;
                foreach (string rawLine in File.ReadAllLines(sourcePath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("//ApiDesc", StringComparison.Ordinal))
                    {
                        pendingDescription = line.Substring("//ApiDesc".Length).Trim();
                        continue;
                    }

                    string methodName = ExtractInterfaceMethodName(line);
                    if (methodName == null)
                        continue;

                    if (!string.IsNullOrWhiteSpace(pendingDescription))
                        docs.Descriptions[methodName] = pendingDescription;
                    docs.Signatures[methodName] = FormatSourceSignature(line);
                    docs.ReturnValues[methodName] = FormatSourceReturnValue(line);
                    pendingDescription = null;
                }
            }
            catch (Exception)
            {
                return new ApiInterfaceDocSet();
            }

            return docs;
        }

        private static string FindApiInterfaceSourcePath()
        {
            string relativePath = Path.Combine("OpenSim", "Region", "ScriptEngine", "Shared", "Api", "Interface", "ILSL_Api.cs");
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSearchRoots(roots, AppDomain.CurrentDomain.BaseDirectory);
            AddSearchRoots(roots, Environment.CurrentDirectory);

            foreach (string root in roots)
            {
                string candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }

            return string.Empty;
        }

        private static void AddSearchRoots(HashSet<string> roots, string start)
        {
            if (string.IsNullOrWhiteSpace(start))
                return;

            DirectoryInfo directory = new DirectoryInfo(start);
            while (directory != null)
            {
                roots.Add(directory.FullName);
                directory = directory.Parent;
            }
        }

        private static string ExtractInterfaceMethodName(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.EndsWith(";", StringComparison.Ordinal))
                return null;

            int paren = line.IndexOf('(');
            if (paren < 0)
                return null;

            string beforeParen = line.Substring(0, paren).Trim();
            int lastSpace = beforeParen.LastIndexOf(' ');
            if (lastSpace < 0 || lastSpace >= beforeParen.Length - 1)
                return null;

            string name = beforeParen.Substring(lastSpace + 1);
            return name.StartsWith("ll", StringComparison.Ordinal) ? name : null;
        }

        private static string FormatSourceSignature(string line)
        {
            string declaration = line.Trim().TrimEnd(';').Trim();
            int paren = declaration.IndexOf('(');
            int close = declaration.LastIndexOf(')');
            if (paren < 0 || close < paren)
                return declaration;

            string beforeParen = declaration.Substring(0, paren).Trim();
            string[] beforeParts = beforeParen.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (beforeParts.Length < 2)
                return declaration;

            string returnType = FormatSourceLslType(beforeParts[beforeParts.Length - 2]);
            string methodName = beforeParts[beforeParts.Length - 1];
            string parameters = declaration.Substring(paren + 1, close - paren - 1).Trim();
            StringBuilder signature = new StringBuilder();
            signature.Append(returnType).Append(' ').Append(methodName).Append('(');

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                string[] parameterParts = parameters.Split(',');
                for (int i = 0; i < parameterParts.Length; ++i)
                {
                    if (i > 0)
                        signature.Append(", ");

                    string parameter = parameterParts[i].Trim();
                    string[] tokens = parameter.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2)
                        signature.Append(FormatSourceLslType(tokens[0])).Append(' ').Append(tokens[1]);
                    else
                        signature.Append(parameter);
                }
            }

            signature.Append(')');
            return signature.ToString();
        }

        private static string FormatSourceReturnValue(string line)
        {
            string declaration = line.Trim();
            int paren = declaration.IndexOf('(');
            if (paren < 0)
                return "Return value follows the ILSL_Api declaration.";

            string beforeParen = declaration.Substring(0, paren).Trim();
            string[] beforeParts = beforeParen.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (beforeParts.Length < 2)
                return "Return value follows the ILSL_Api declaration.";

            string returnType = FormatSourceLslType(beforeParts[beforeParts.Length - 2]);
            if (returnType == "void")
                return "No return value.";

            return "Returns " + returnType + ".";
        }

        private static string FormatSourceLslType(string type)
        {
            switch (type)
            {
                case "void":
                    return "void";
                case "int":
                case "uint":
                case "short":
                case "ushort":
                case "byte":
                case "sbyte":
                case "bool":
                case "LSL_Integer":
                    return "integer";
                case "double":
                case "float":
                case "LSL_Float":
                    return "float";
                case "string":
                case "LSL_String":
                    return "string";
                case "LSL_Key":
                    return "key";
                case "LSL_List":
                    return "list";
                case "LSL_Vector":
                    return "vector";
                case "LSL_Rotation":
                    return "rotation";
                default:
                    return type;
            }
        }

        private static string FormatScriptFunctionSignature(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            StringBuilder signature = new StringBuilder();
            signature.Append(FormatLslType(method.ReturnType, method.Name)).Append(' ').Append(method.Name).Append('(');

            for (int i = 0; i < parameters.Length; ++i)
            {
                if (i > 0)
                    signature.Append(", ");

                ParameterInfo parameter = parameters[i];
                signature.Append(FormatLslType(parameter.ParameterType, parameter.Name))
                    .Append(' ')
                    .Append(parameter.Name ?? ("arg" + i.ToString(CultureInfo.InvariantCulture)));
            }

            signature.Append(')');
            return signature.ToString();
        }

        private static string FormatScriptFunctionReturn(MethodInfo method)
        {
            if (method.ReturnType == typeof(void))
                return "No return value.";

            return "Returns " + FormatLslType(method.ReturnType, method.Name) + ".";
        }

        private static string FormatLslType(Type type, string nameHint)
        {
            if (type == typeof(void))
                return "void";
            if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool))
                return "integer";
            if (type == typeof(float) || type == typeof(double))
                return "float";
            if (type == typeof(string))
                return LooksLikeKeyName(nameHint) ? "key" : "string";

            string typeName = type.FullName ?? type.Name;
            if (typeName.EndsWith("LSLInteger", StringComparison.Ordinal) || typeName.EndsWith("LSL_Types+LSLInteger", StringComparison.Ordinal))
                return "integer";
            if (typeName.EndsWith("LSLFloat", StringComparison.Ordinal) || typeName.EndsWith("LSL_Types+LSLFloat", StringComparison.Ordinal))
                return "float";
            if (typeName.EndsWith("LSLString", StringComparison.Ordinal) || typeName.EndsWith("LSL_Types+LSLString", StringComparison.Ordinal))
                return LooksLikeKeyName(nameHint) ? "key" : "string";
            if (type.Name.Equals("list", StringComparison.OrdinalIgnoreCase))
                return "list";
            if (typeName.EndsWith("Vector3", StringComparison.Ordinal))
                return "vector";
            if (typeName.EndsWith("Quaternion", StringComparison.Ordinal))
                return "rotation";

            return type.Name;
        }

        private static bool LooksLikeKeyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string lower = name.ToLowerInvariant();
            return lower.Contains("key")
                || lower.Contains("id")
                || lower.Contains("uuid")
                || lower.Contains("agent")
                || lower.Contains("avatar")
                || lower.Contains("object")
                || lower.Contains("owner")
                || lower.Contains("target")
                || lower.Contains("destination")
                || lower.StartsWith("llrequest", StringComparison.Ordinal)
                || lower.StartsWith("llgetkey", StringComparison.Ordinal)
                || lower.StartsWith("llgeneratekey", StringComparison.Ordinal);
        }

        private sealed class ApiInterfaceDocSet
        {
            public readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ReturnValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static ScriptFunctionDoc Doc(string category, string name, string signature, string returnValue, string usage, string permissions, string notes)
        {
            return new ScriptFunctionDoc
            {
                Category = category,
                Name = name,
                Signature = signature,
                ReturnValue = returnValue,
                Usage = usage,
                Permissions = permissions,
                Notes = notes,
                Example = string.Empty
            };
        }

        private class RegionPageContent
        {
            public string Title;
            public string Tagline;
            public string Description;
            public string HeroImage;
            public readonly List<GalleryItem> Gallery = new List<GalleryItem>();
        }

        private class EstatePageContent
        {
            public string Title;
            public string Tagline;
            public string Description;
            public string HeroImage;
            public readonly List<FeatureItem> Features = new List<FeatureItem>();
        }

        private class FeatureItem
        {
            public string Title;
            public string Body;
        }

        private class FeaturePageContent
        {
            public string Title;
            public string Summary;
            public string Overview;
            public List<string> Usage = new List<string>();
            public List<string> Notes = new List<string>();
        }

        private class ScriptFunctionDoc
        {
            public string Category;
            public string Name;
            public string Signature;
            public string ReturnValue;
            public string Usage;
            public string Permissions;
            public string Notes;
            public string Example;
        }

        private class GalleryItem
        {
            public string FileName;
            public string Caption;
        }

        private class BlogPost
        {
            public string Title;
            public string Slug;
            public DateTime Date;
            public string Summary;
            public string Image;
            public string Body;
        }

        private class CurrencyLoginChallenge
        {
            public UUID AgentID;
            public string DisplayName;
            public string Token;
            public DateTime ExpiresUTC;
            public bool IsAdmin;
        }

        private class CurrencyWebSession
        {
            public UUID AgentID;
            public string DisplayName;
            public string CsrfToken;
            public DateTime ExpiresUTC;
            public bool IsAdmin;
        }

        private class CurrencyPurchaseRequest
        {
            public string RequestID;
            public DateTime RequestedUTC;
            public UUID AgentID;
            public string DisplayName;
            public int Amount;
            public string Status;
            public DateTime UpdatedUTC;
            public string OperatorName;
            public string Note;
        }

        private class CurrencyPayPalOrder
        {
            public string LocalID;
            public string PayPalOrderID;
            public UUID AgentID;
            public string DisplayName;
            public int TokenAmount;
            public decimal FiatAmount;
            public string CurrencyCode;
            public string Status;
            public DateTime CreatedUTC;
            public DateTime UpdatedUTC;
            public string Note;
        }

        private class InventoryCarouselItem
        {
            public UUID AssetID;
            public string Name;
            public int CreationDate;
        }

        private class InventoryCarouselAssetCacheEntry
        {
            public byte[] Data;
            public string ContentType;
            public DateTime ExpiresUTC;
        }

        private class RegionWebStats
        {
            public int RootAgents;
            public int ChildAgents;
            public int NPCs;
            public int Objects;
            public int Prims;
            public int MeshParts;
            public int SculptParts;
            public int ParcelCount;
            public float SimFPS;
            public readonly List<ParcelSummary> Parcels = new List<ParcelSummary>();
        }

        private class EstateStats
        {
            public int RegionCount;
            public int RootAgents;
            public int ChildAgents;
            public int NPCs;
            public int Objects;
            public int Prims;
            public int MeshParts;
            public int SculptParts;
            public int ParcelCount;
        }

        private class ParcelSummary
        {
            public string Name;
            public int Area;
        }
    }
}
