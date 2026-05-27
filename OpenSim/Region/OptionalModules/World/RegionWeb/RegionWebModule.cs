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
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;

namespace OpenSim.Region.OptionalModules.World.RegionWeb
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionWebModule")]
    public class RegionWebModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object m_sync = new object();
        private readonly Dictionary<UUID, Scene> m_scenesByID = new Dictionary<UUID, Scene>();
        private readonly Dictionary<string, UUID> m_regionIDsBySlug = new Dictionary<string, UUID>(StringComparer.OrdinalIgnoreCase);

        private bool m_enabled;
        private bool m_handlerRegistered;
        private bool m_autoCreateContent;
        private bool m_showMap;
        private bool m_showStats;
        private bool m_showParcels;
        private int m_postsPerPage;
        private string m_defaultEstateTitle = "OpenSimulator Estate";
        private string m_basePath = "/regionweb";
        private string m_contentDirectory = "RegionWeb";
        private string m_absoluteContentDirectory;

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
            m_defaultEstateTitle = config.GetString("EstateTitle", "OpenSimulator Estate").Trim();
            if (string.IsNullOrEmpty(m_defaultEstateTitle))
                m_defaultEstateTitle = "OpenSimulator Estate";

            if (string.IsNullOrEmpty(m_contentDirectory))
                m_contentDirectory = "RegionWeb";

            m_absoluteContentDirectory = Path.IsPathRooted(m_contentDirectory)
                ? m_contentDirectory
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_contentDirectory);
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

                IHttpServer server = MainServer.GetHttpServer(0);
                server.AddSimpleStreamHandler(new SimpleStreamHandler(m_basePath, HandleRequest, "RegionWeb"));
                server.AddSimpleStreamHandler(new SimpleStreamHandler(m_basePath, HandleRequest, "RegionWeb"), true);
                m_handlerRegistered = true;

                MainConsole.Instance.Commands.AddCommand(
                    "RegionWeb", false, "regionweb show",
                    "regionweb show",
                    "Show public RegionWeb URLs and content folders for loaded regions.",
                    HandleShowCommand);

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
                response.RawBuffer = Encoding.UTF8.GetBytes("RegionWeb request failed.");
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

            StringBuilder html = BeginPage(content.Title);
            html.Append("<header class=\"estate-hero");
            if (string.IsNullOrEmpty(content.HeroImage))
                html.Append(" estate-hero-plain");
            html.Append("\"");
            if (!string.IsNullOrEmpty(content.HeroImage))
            {
                html.Append(" style=\"background-image:linear-gradient(90deg,rgba(8,18,22,.86),rgba(8,18,22,.34)),url('")
                    .Append(Html(EstateMediaURL(content.HeroImage))).Append("')\"");
            }

            html.Append("><div class=\"wrap\"><p>").Append(Html(content.Tagline)).Append("</p><h1>")
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
            html.Append("<main class=\"wrap feature-page\"><a class=\"back\" href=\"")
                .Append(Html(m_basePath)).Append("/#features\">Back to features</a>")
                .Append("<p class=\"feature-kicker\">Feature guide</p><h1>")
                .Append(Html(content.Title)).Append("</h1><p class=\"lead\">")
                .Append(Html(content.Summary)).Append("</p>");

            html.Append("<section><h2>What it does</h2>")
                .Append(Paragraphs(content.Overview)).Append("</section>");

            AppendFeatureList(html, "How to use it", content.Usage);
            AppendFeatureList(html, "Configuration notes", content.Notes);

            html.Append("</main>").Append(EndPage());
            SendHtml(response, html.ToString());
        }

        private void SendRegionPage(Scene scene, IOSHttpResponse response)
        {
            RegionPageContent content = LoadContent(scene);
            RegionWebStats stats = GetStats(scene);
            List<BlogPost> posts = LoadPosts(scene).Take(m_postsPerPage).ToList();
            string slug = MakeSlug(scene.RegionInfo.RegionName);

            StringBuilder html = BeginPage(content.Title);
            html.Append("<header class=\"hero\" style=\"background-image:linear-gradient(90deg,rgba(8,18,22,.80),rgba(8,18,22,.30)),url('")
                .Append(Html(GetHeroURL(scene, content))).Append("')\">")
                .Append("<div class=\"wrap\"><p>").Append(Html(content.Tagline)).Append("</p>")
                .Append("<h1>").Append(Html(content.Title)).Append("</h1>")
                .Append("<div class=\"meta\">").Append(Html(scene.RegionInfo.RegionSizeX.ToString(CultureInfo.InvariantCulture)))
                .Append(" x ").Append(Html(scene.RegionInfo.RegionSizeY.ToString(CultureInfo.InvariantCulture)))
                .Append(" m &middot; grid ").Append(Html(scene.RegionInfo.RegionLocX.ToString(CultureInfo.InvariantCulture)))
                .Append(", ").Append(Html(scene.RegionInfo.RegionLocY.ToString(CultureInfo.InvariantCulture))).Append("</div></div></header>");

            html.Append("<main class=\"wrap layout\">");
            html.Append("<section class=\"story\">").Append(Paragraphs(content.Description));

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
                AppendStats(html, stats);

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
            html.Append("<main class=\"wrap post-page\"><a class=\"back\" href=\"")
                .Append(Html(m_basePath)).Append("/").Append(Url(slug)).Append("/\">Back to ")
                .Append(Html(content.Title)).Append("</a><article class=\"post full\">");

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

        private EstatePageContent LoadEstateContent()
        {
            EstatePageContent content = new EstatePageContent();
            content.Title = m_defaultEstateTitle;
            content.Tagline = "A modern OpenSimulator estate";
            content.Description = "This estate runs a tuned OpenSim build with richer maps, better region presentation, weather, visitor tools and simulator polish.";
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
                "Estate builders can use text commands connected to AI to plan, generate and refine terrain or building ideas directly from the simulator workflow.");
            EnsureFeature(content.Features, "Automatic cloud avatar recovery",
                "If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds.");
            EnsureFeature(content.Features, "Group auto invite",
                "Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.");
            EnsureFeature(content.Features, "Viewer polish",
                "Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent.");
            EnsureFeature(content.Features, "Experience-Lite script permissions",
                "Trusted estate scripts can receive selected persistent permissions without repeatedly interrupting visitors with viewer popups.");

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

            content.Title = config.GetString("Title", content.Title).Trim();
            content.Summary = config.GetString("Summary", content.Summary).Trim();
            content.Overview = config.GetString("Overview", content.Overview).Trim();

            List<string> usage = ParseFeatureList(config, "Usage");
            if (usage.Count > 0)
                content.Usage = usage;

            List<string> notes = ParseFeatureList(config, "Note");
            if (notes.Count > 0)
                content.Notes = notes;

            return content;
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
                    content.Title = "RegionWeb estate portal";
                    content.Summary = "Every estate and region can publish a web page with photos, posts, map tiles, parcels and live simulator statistics.";
                    content.Overview = "RegionWeb turns the simulator HTTP endpoint into a simple estate website. The central page lists online regions and estate features; each region gets a profile page, gallery, blog posts, current map tile, parcel summaries and live stats pulled from the simulator.";
                    content.Usage.Add("Open /regionweb/ on the simulator HTTP address to view the estate landing page.");
                    content.Usage.Add("Edit bin/RegionWeb/estate.ini for the central title, tagline, hero image and feature cards.");
                    content.Usage.Add("Edit bin/RegionWeb/<region-slug>/profile.ini for each region page, and add JPEG or PNG files under that region's media folder.");
                    content.Usage.Add("Create posts as text files under bin/RegionWeb/<region-slug>/posts/ using the Title, Date, Summary, Image and body format created by the sample file.");
                    content.Notes.Add("The module auto-creates starter folders without overwriting existing content.");
                    break;

                case "weather-and-visitor-polish":
                case "weather-module":
                    content.Title = "Weather module";
                    content.Summary = "Regions can run rain, storm, snow or sunny presets with wind, clouds, lightning, thunder and automatic forecast cycling.";
                    content.Overview = "The weather system adds estate-controlled atmosphere without needing scripted emitters scattered by hand. It can change clouds and wind, spawn particle weather, announce forecasts and cycle between presets after startup.";
                    content.Usage.Add("Enable the module in [Weather] and choose the command channel used by estate managers.");
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
                    content.Usage.Add("Use it for planning, layout, terrain and fast creative iteration, then review the generated changes like any other build work.");
                    content.Notes.Add("AI-assisted building should stay permission-aware: restrict access to trusted builders or estate staff.");
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

                case "viewer-polish":
                    content.Overview = "Viewer-facing polish keeps neighbouring regions feeling like one estate. The simulator can send a stable branded version string to viewers so teleports and crossings do not produce noisy different-version warnings when local builds or operating systems differ.";
                    content.Usage.Add("Set SendSimulatorVersionToViewer and ViewerSimulatorVersionOverride in [ClientStack.LindenUDP].");
                    content.Usage.Add("Use the same override string across estate regions that should feel like one coherent grid experience.");
                    content.Notes.Add("This is presentation polish only; keep the actual simulator binaries compatible for crossings and shared services.");
                    break;

                case "experience-lite-script-permissions":
                    content.Overview = "Experience-Lite adds an estate-controlled trust layer for script runtime permissions. Trusted owners or objects can auto-grant selected permissions such as controls, animations, camera, teleport and animation override, so visitors are not repeatedly interrupted by permission dialogs for estate systems they already trust.";
                    content.Usage.Add("Enable [ScriptExperiences] only in trusted estate environments.");
                    content.Usage.Add("Add trusted script owner UUIDs to TrustedOwners, or specific root object/prim UUIDs to TrustedObjects.");
                    content.Usage.Add("Keep AutoGrantPermissions limited to the permissions your estate systems actually need.");
                    content.Usage.Add("Use llRequestPermissions normally from scripts; trusted requests are granted automatically when covered by the configured bitmask.");
                    content.Notes.Add("The default bitmask excludes PERMISSION_DEBIT and ownership changes.");
                    content.Notes.Add("Untrusted scripts keep the normal viewer permission prompt behavior.");
                    break;
            }

            return content;
        }

        private RegionPageContent LoadContent(Scene scene)
        {
            RegionPageContent content = new RegionPageContent();
            content.Title = scene.RegionInfo.RegionName;
            content.Tagline = "A region in OpenSimulator";
            content.Description = "Add region photos and a description in this region's RegionWeb content folder.";
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
                + "Tagline = \"Gunthar OpenSim, polished for creators and visitors\"\n"
                + "Description = \"A public estate portal for regions, maps, news and technical improvements. This build keeps OpenSim's flexibility while adding a cleaner visitor experience, better cartography, richer presentation pages and smoother simulator startup behavior.\"\n"
                + "HeroImage = \"\"\n"
                + "; Feature entries use title|description.\n"
                + "Feature1 = \"High quality world map|Terrain textures, water depth shading, land detail, aerial tone mapping, mesh/sculpt geometry projection, cleaner water alpha handling, background generation and cooperative rendering make map tiles sharper, more geographic and safer for simulator responsiveness.\"\n"
                + "Feature2 = \"RegionWeb estate portal|Every region can have a public web page with photos, blog posts, map tile, parcels and live region statistics.\"\n"
                + "Feature3 = \"Weather module|Regions can run rain, storm, snow or sunny presets, with wind, clouds, lightning, thunder and automatic forecast cycling.\"\n"
                + "Feature4 = \"Wave-following boats|Boats can now move with the sea surface, following wave motion for a more natural marina and sailing experience.\"\n"
                + "Feature5 = \"Smooth region crossings|Avatar and vehicle crossings between neighbouring regions are smoothed to reduce the hard stop, rubber-banding and visual pop of stock OpenSim border transfers.\"\n"
                + "Feature6 = \"Lag-resistant walk animations|Walking animations recover cleanly after lag spikes, so avatars do not remain stuck in broken walk states when the simulator catches up.\"\n"
                + "Feature7 = \"AI-connected text build tools|Estate builders can use text commands connected to AI to plan, generate and refine terrain or building ideas directly from the simulator workflow.\"\n"
                + "Feature8 = \"Automatic cloud avatar recovery|If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds.\"\n"
                + "Feature9 = \"Group auto invite|Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.\"\n"
                + "Feature10 = \"Viewer polish|Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent.\"\n"
                + "Feature11 = \"Experience-Lite script permissions|Trusted estate scripts can receive selected persistent permissions without repeatedly interrupting visitors with viewer popups.\"\n",
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
                    + "This is the first RegionWeb post. Replace this text with news, build notes, events, credits, or travel information for visitors.\n",
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

                if (feature.Title.Equals("Text build tools", StringComparison.OrdinalIgnoreCase))
                {
                    normalized.Add(new FeatureItem
                    {
                        Title = "AI-connected text build tools",
                        Body = "Estate builders can use text commands connected to AI to plan, generate and refine terrain or building ideas directly from the simulator workflow."
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

        private static void AddDefaultFeatures(List<FeatureItem> features)
        {
            features.Add(new FeatureItem
            {
                Title = "High quality world map",
                Body = "Terrain textures, water depth shading, land detail, aerial tone mapping, mesh/sculpt geometry projection, cleaner water alpha handling, background generation and cooperative rendering make map tiles sharper, more geographic and safer for simulator responsiveness."
            });
            features.Add(new FeatureItem
            {
                Title = "RegionWeb estate portal",
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
                Body = "Estate builders can use text commands connected to AI to plan, generate and refine terrain or building ideas directly from the simulator workflow."
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
                Title = "Viewer polish",
                Body = "Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent."
            });
            features.Add(new FeatureItem
            {
                Title = "Experience-Lite script permissions",
                Body = "Trusted estate scripts can receive selected persistent permissions without repeatedly interrupting visitors with viewer popups."
            });
        }

        private static void EnsureFeature(List<FeatureItem> features, string title, string body)
        {
            foreach (FeatureItem feature in features)
            {
                if (feature.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                    return;
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

        private StringBuilder BeginPage(string title)
        {
            StringBuilder html = new StringBuilder(8192);
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<title>").Append(Html(title)).Append("</title>")
                .Append("<style>")
                .Append("body{margin:0;background:#101417;color:#e9efec;font:16px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}a{color:#9bd3e6;text-decoration:none}img{max-width:100%;display:block}.wrap{max-width:1180px;margin:0 auto;padding:0 24px}.estate-hero{min-height:520px;background-size:cover;background-position:center;display:flex;align-items:flex-end}.estate-hero-plain{background:linear-gradient(135deg,#11252b,#1e2927 52%,#3a3526)}.estate-hero .wrap{padding-top:110px;padding-bottom:72px}.estate-hero p{max-width:760px;color:#d9e5e1;font-size:19px}.estate-hero>div>p:first-child,.hero p,.feature-kicker{margin:0 0 10px;color:#b9d8d3;text-transform:uppercase;font-size:13px;letter-spacing:.08em}.estate-hero h1{max-width:900px;margin:0;font-size:clamp(44px,8vw,96px);line-height:.92}.estate-actions{display:flex;flex-wrap:wrap;gap:12px;margin-top:28px}.estate-actions a{background:#d7e4df;color:#101417;padding:10px 15px;font-weight:700}.estate-actions a+a{background:#223239;color:#dbe7e4}.estate-stats{display:grid;grid-template-columns:repeat(5,1fr);gap:1px;margin-top:28px;background:#2a363a}.estate-stats div{background:#171e22;padding:18px}.estate-stats strong{display:block;font-size:30px}.estate-stats span{color:#aebbb9}.feature-section{padding-top:48px}.feature-section h2,.list h2{font-size:34px;margin:0 0 20px}.feature-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px}.feature-card{display:block;background:#171e22;border:1px solid #263136;color:#e9efec;padding:18px;min-height:190px}.feature-card:hover{border-color:#6da8b7;background:#1a2428}.feature-card h3{margin:0 0 8px;font-size:21px}.feature-card p{margin:0;color:#c7d2cf}.feature-card span{display:inline-block;margin-top:18px;color:#9bd3e6;font-weight:700}.feature-page{padding-top:42px;padding-bottom:70px;max-width:900px}.feature-page h1{font-size:clamp(38px,7vw,68px);line-height:1;margin:0 0 18px}.feature-page .lead{font-size:21px;color:#d4dfdc;margin:0 0 34px}.feature-page section{border-top:1px solid #2a363a;padding-top:24px;margin-top:26px}.feature-page h2{font-size:28px;margin:0 0 12px}.feature-page li{margin:0 0 10px;color:#d2dcda}.hero{min-height:360px;background-size:cover;background-position:center;display:flex;align-items:flex-end}.hero .wrap{padding-top:90px;padding-bottom:46px}.hero h1{margin:0;font-size:clamp(38px,7vw,82px);line-height:.94}.meta{margin-top:16px;color:#cfd8d5}.layout{display:grid;grid-template-columns:minmax(0,1fr) 340px;gap:36px;padding-top:36px;padding-bottom:56px}.story{min-width:0}.story>p{font-size:19px;color:#d5dfdc}.gallery{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px;margin:30px 0}.gallery figure{margin:0;background:#182025}.gallery img{aspect-ratio:4/3;object-fit:cover}.gallery figcaption{padding:10px;color:#c7d0ce;font-size:14px}.panel{align-self:start}.map{width:100%;aspect-ratio:1;object-fit:cover;border:1px solid #2a363a}.stats,.parcels{margin-top:18px;background:#171e22;border:1px solid #263136;padding:18px}.stats h2,.parcels h2,.story h2{margin:0 0 14px}.stats dl{display:grid;grid-template-columns:1fr auto;gap:7px 16px;margin:0}.stats dt{color:#9facad}.stats dd{margin:0;font-weight:700}.parcels div{display:flex;justify-content:space-between;gap:12px;border-top:1px solid #263136;padding:9px 0}.parcels div:first-of-type{border-top:0}.parcels span{color:#aab6b8}.post{border-top:1px solid #2a363a;padding:22px 0}.post img{width:100%;max-height:360px;object-fit:cover;margin-bottom:14px}.post time{color:#9facad;font-size:13px}.post h3{margin:4px 0 8px;font-size:24px}.post p{color:#cbd5d2}.post-page{padding-top:36px;padding-bottom:60px;max-width:850px}.post.full h1{font-size:46px;line-height:1.05;margin:6px 0 22px}.post.full p{font-size:18px}.back{display:inline-block;margin-bottom:18px}.region-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:18px}.list{padding-top:42px;padding-bottom:60px}.region-card{background:#171e22;border:1px solid #263136;color:#e9efec}.region-card img{aspect-ratio:16/9;object-fit:cover}.region-card strong,.region-card span{display:block;padding:0 14px}.region-card strong{padding-top:13px;font-size:20px}.region-card span{padding-bottom:14px;color:#abb8b8}.empty code{word-break:break-all}@media(max-width:820px){.layout,.estate-stats{grid-template-columns:1fr}.hero{min-height:300px}.estate-hero{min-height:430px}.wrap{padding-left:16px;padding-right:16px}}")
                .Append("</style></head><body>");
            return html;
        }

        private static string EndPage()
        {
            return "</body></html>";
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
