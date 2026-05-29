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
        private const string ScriptEngineFeatureTitle = "Second Life-style script engine";
        private const string ScriptEngineFeatureBody =
            "The script engine is moving closer to Second Life behavior with Experience-Lite permissions, scripted sit controls, key-value stores, linkset data, environment, estate-return, parcel media, inventory transfer, damage, RSA, attachment filter, GLTF material and physics primitive-param helpers.";
        private const string ScriptEngineFeatureOverview =
            "The script engine now includes a wider Second Life-style scripting surface for modern estate systems. Trusted estate scripts can use Experience-Lite permissions, persistent experience key-value storage, linkset data with linkset_data events, scripted sit controls, linked sound controls, region and parcel environment helpers, estate return and terrain helpers, parcel media controls, direct inventory and ownership transfer, direct damage helpers, GLTF/render material primitive params with stored override readback, physics material primitive params, secure hashing/HMAC/RSA helpers, parameterized rez/derez workflows, filtered attachment inspection and HUD coordinate helpers without relying on brittle scripted workarounds. Second Life pathfinding character calls are exposed for script compatibility and post no-navmesh path_update failures instead of pretending to move objects.";

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

                if (parts[0].Equals("scripts", StringComparison.OrdinalIgnoreCase))
                {
                    SendScriptReference(parts.Length >= 2 ? parts[1] : string.Empty, response);
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
                .Append("<div class=\"estate-actions\"><a href=\"#regions\">Explore regions</a><a href=\"#features\">New features</a><a href=\"")
                .Append(Html(m_basePath)).Append("/scripts\">LSL scripts</a></div>")
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

        private void SendScriptReference(string slug, IOSHttpResponse response)
        {
            EstatePageContent estate = LoadEstateContent();
            ScriptFunctionDoc focus = null;

            if (!string.IsNullOrEmpty(slug))
            {
                foreach (ScriptFunctionDoc doc in ScriptFunctionDocs)
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

            StringBuilder html = BeginPage("LSL Script Function Reference - " + estate.Title);
            html.Append("<main class=\"wrap script-reference\"><a class=\"back\" href=\"")
                .Append(Html(m_basePath)).Append("/#features\">Back to estate</a>")
                .Append("<p class=\"feature-kicker\">Script reference</p><h1>LSL Function Reference</h1>")
                .Append("<p class=\"lead\">Expanded Second Life-style LSL functions implemented or corrected in this OpenSim build, with signatures, return values, permissions and exact in-world usage notes.</p>")
                .Append("<p class=\"script-source\">Modeled after the public Second Life LSL function index, but scoped to the functions exposed by this simulator branch.</p>");

            if (focus != null)
            {
                html.Append("<section class=\"script-focus\">");
                AppendScriptFunctionCard(html, focus, m_basePath, true);
                html.Append("</section></main>").Append(EndPage());
                SendHtml(response, html.ToString());
                return;
            }

            html.Append("<section class=\"script-toc\"><h2>Functions</h2><div>");
            foreach (IGrouping<string, ScriptFunctionDoc> group in ScriptFunctionDocs.GroupBy(doc => doc.Category))
            {
                html.Append("<a href=\"#").Append(Html(MakeSlug(group.Key))).Append("\">")
                    .Append(Html(group.Key)).Append(" <span>")
                    .Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append("</span></a>");
            }
            html.Append("</div></section>");

            foreach (IGrouping<string, ScriptFunctionDoc> group in ScriptFunctionDocs.GroupBy(doc => doc.Category))
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

        private static void AppendScriptFunctionCard(StringBuilder html, ScriptFunctionDoc doc, string basePath, bool focused)
        {
            string slug = MakeSlug(doc.Name);
            html.Append("<article class=\"script-card\" id=\"").Append(Html(slug)).Append("\"><div class=\"script-card-head\"><h3>");
            if (focused)
                html.Append(Html(doc.Name));
            else
                html.Append("<a href=\"").Append(Html(basePath)).Append("/scripts/").Append(Html(slug)).Append("\">").Append(Html(doc.Name)).Append("</a>");

            html.Append("</h3><span>").Append(Html(doc.Category)).Append("</span></div>")
                .Append("<p class=\"signature\"><code>").Append(Html(doc.Signature)).Append("</code></p>");

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
                "Estate builders can use text commands connected to AI or uploaded cartography textures to plan, generate and refine terrain or building ideas directly from the simulator workflow.");
            EnsureFeature(content.Features, "Automatic cloud avatar recovery",
                "If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds.");
            EnsureFeature(content.Features, "Group auto invite",
                "Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.");
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
                    content.Usage.Add("Use llGetEnvironment(), llReplaceEnvironment(), llReplaceAgentEnvironment(), llSetEnvironment() and llSetAgentEnvironment() for supported EEP day-cycle, parcel, region and agent environment workflows.");
                    content.Usage.Add("Use llReturnObjectsByID(), llReturnObjectsByOwner(), OBJECT_RETURN_* and PERMISSION_RETURN_OBJECTS for scripted estate/parcel cleanup.");
                    content.Usage.Add("Use llSetParcelForSale(forSale, options), PARCEL_SALE_* and PERMISSION_PRIVILEGED_LAND_ACCESS for scripted parcel sale workflows when the script owner owns the parcel.");
                    content.Usage.Add("Use llParcelMediaCommandList(), llParcelMediaQuery(), PARCEL_MEDIA_COMMAND_LOOP_SET, media description/type/size and auto-align fields for SL-style parcel media controllers.");
                    content.Usage.Add("Use llSetGroundTexture() with TERRAIN_DETAIL_* and TERRAIN_HEIGHT_RANGE_* to update estate terrain textures and blending heights.");
                    content.Usage.Add("Use llSetRenderMaterial(), llSetLinkRenderMaterial(), llSetLinkGLTFOverrides(), llGetRenderMaterial(), llIsLinkGLTFMaterial(), PRIM_RENDER_MATERIAL, PRIM_GLTF_* and OVERRIDE_GLTF_* constants for PBR/material-aware content, including primitive-param render-material set/get and PRIM_GLTF_* set/readback for stored GLTF override data.");
                    content.Usage.Add("Use PRIM_PHYSICS_MATERIAL through llSetPrimitiveParams(), llGetPrimitiveParams(), llSetLinkPrimitiveParams() and llGetLinkPrimitiveParams() for SL-order gravity, restitution, friction and density workflows.");
                    content.Usage.Add("Use llMatchGroup(agent, group_keys) for same-region active-group checks without scripted llSameGroup relay objects.");
                    content.Usage.Add("Use llSetDamage(), llDamage(), llGetHealth(), PRIM_DAMAGE, PRIM_HEALTH, OBJECT_HEALTH, OBJECT_DAMAGE, OBJECT_DAMAGE_TYPE and DAMAGE_TYPE_* constants for supported health/damage workflows.");
                    content.Usage.Add("Pathfinding scripts can compile against llCreateCharacter(), llNavigateTo(), llGetStaticPath() and related CHARACTER_* APIs; OpenSim posts PU_FAILURE_NO_NAVMESH path_update events instead of simulating fake movement.");
                    content.Usage.Add("Use llHMAC(), llComputeHash(), llSignRSA() and llVerifyRSA() for signature checks, web callbacks and secure scripted handshakes.");
                    content.Usage.Add("Use llGetAttachedListFiltered(), ATTACH_ANY_HUD, FILTER_INCLUDE and FILTER_FLAG_HUDS for filtered attachment queries.");
                    content.Usage.Add("Use llDetectedRezzer() from sensor/collision/touch-style detected data when scripts need to identify object provenance.");
                    content.Usage.Add("Use llFindNotecardTextSync() for cached synchronous notecard text search.");
                    content.Usage.Add("Use llGiveAgentInventory(), TRANSFER_DEST, TRANSFER_FLAGS, TRANSFER_* result codes, llTransferOwnership(), TRANSFER_FLAG_COPY and TRANSFER_FLAG_TAKE for SL-style direct delivery and ownership workflows where estate trust and simulator support allow them.");
                    content.Usage.Add("Use the bundled script-engine examples, including the PBR GLTF physics primitive-param lab, to verify each newly implemented LSL feature in-world.");
                    content.Usage.Add("Use llWorldPosToHUD() for HUDs that need to point at or track in-world positions.");
                    content.Usage.Add("Use llGetStartString() when scripts need SL-style start parameter data after rez.");
                    content.Usage.Add("llSetSculptAnim() is exposed for script compatibility; OpenSim currently has no sculpt-map animation backend.");
                    content.Usage.Add("Open the RegionWeb scripts page at /regionweb/scripts for the per-function reference with signatures, return values, permissions and usage notes.");
                    content.Notes.Add("The default permission bitmask excludes PERMISSION_DEBIT and ownership changes.");
                    content.Notes.Add("Untrusted scripts keep the normal viewer permission prompt behavior.");
                    content.Notes.Add("The store is scoped per region/owner and persisted under KeyValueStorePath, making it useful for estate tools, games, rides and AI build workflows.");
                    content.Notes.Add("Use KeyValueStoreMaxKeys, KeyValueStoreMaxKeyBytes, KeyValueStoreMaxValueBytes, KeyValueStoreMaxStoreBytes and KeyValueStorePath to tune storage.");
                    content.Notes.Add("New constants documented by the script runtime include XP_ERROR_*, SIT_*, SIT_FLAG_*, LINKSETDATA_*, SOUND_*, REZ_*, REZ_FLAG_*, PARCEL_SALE_*, PARCEL_MEDIA_COMMAND_*, OBJECT_RETURN_*, ENV_*, SKY_*, WATER_*, TERRAIN_*, TRANSFER_*, FILTER_*, DAMAGE_TYPE_*, CHARACTER_*, PU_*, PRIM_SCRIPTED_SIT_ONLY, PRIM_ALLOW_UNSIT, PRIM_SIT_TARGET, PRIM_RENDER_MATERIAL, PRIM_GLTF_*, OVERRIDE_GLTF_*, PRIM_PHYSICS_MATERIAL and CHANGED_RENDER_MATERIAL.");
                    content.Notes.Add("Pathfinding/character APIs, sculpt-map animation and per-parameter EEP override persistence still require deeper simulator modules and are intentionally not advertised as complete.");
                    content.Notes.Add("Existing RegionWeb feature files are merged with these built-in defaults at render time, so older auto-generated pages pick up the newer LSL surface without deleting local notes.");
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
                + "Feature7 = \"AI-connected text build tools|Estate builders can use text commands connected to AI or uploaded cartography textures to plan, generate and refine terrain or building ideas directly from the simulator workflow.\"\n"
                + "Feature8 = \"Automatic cloud avatar recovery|If an avatar becomes a cloud, the server automatically handles the recovery and restores the normal appearance within a few seconds.\"\n"
                + "Feature9 = \"Group auto invite|Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.\"\n"
                + "Feature10 = \"Viewer polish|Simulator version branding reduces noisy viewer warnings and keeps neighbouring regions feeling consistent.\"\n"
                + "Feature11 = \"" + ScriptEngineFeatureTitle + "|" + ScriptEngineFeatureBody + "\"\n",
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
            bool scriptEngineFeatureAdded = false;

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
                    if (IsScriptEngineFeature(title))
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

        private StringBuilder BeginPage(string title)
        {
            StringBuilder html = new StringBuilder(8192);
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<title>").Append(Html(title)).Append("</title>")
                .Append("<style>")
                .Append("body{margin:0;background:#101417;color:#e9efec;font:16px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}a{color:#9bd3e6;text-decoration:none}img{max-width:100%;display:block}.wrap{max-width:1180px;margin:0 auto;padding:0 24px}.estate-hero{min-height:520px;background-size:cover;background-position:center;display:flex;align-items:flex-end}.estate-hero-plain{background:linear-gradient(135deg,#11252b,#1e2927 52%,#3a3526)}.estate-hero .wrap{padding-top:110px;padding-bottom:72px}.estate-hero p{max-width:760px;color:#d9e5e1;font-size:19px}.estate-hero>div>p:first-child,.hero p,.feature-kicker{margin:0 0 10px;color:#b9d8d3;text-transform:uppercase;font-size:13px;letter-spacing:.08em}.estate-hero h1{max-width:900px;margin:0;font-size:clamp(44px,8vw,96px);line-height:.92}.estate-actions{display:flex;flex-wrap:wrap;gap:12px;margin-top:28px}.estate-actions a{background:#d7e4df;color:#101417;padding:10px 15px;font-weight:700}.estate-actions a+a{background:#223239;color:#dbe7e4}.estate-stats{display:grid;grid-template-columns:repeat(5,1fr);gap:1px;margin-top:28px;background:#2a363a}.estate-stats div{background:#171e22;padding:18px}.estate-stats strong{display:block;font-size:30px}.estate-stats span{color:#aebbb9}.feature-section{padding-top:48px}.feature-section h2,.list h2{font-size:34px;margin:0 0 20px}.feature-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px}.feature-card{display:block;background:#171e22;border:1px solid #263136;color:#e9efec;padding:18px;min-height:190px}.feature-card:hover{border-color:#6da8b7;background:#1a2428}.feature-card h3{margin:0 0 8px;font-size:21px}.feature-card p{margin:0;color:#c7d2cf}.feature-card span{display:inline-block;margin-top:18px;color:#9bd3e6;font-weight:700}.feature-page{padding-top:42px;padding-bottom:70px;max-width:900px}.feature-page h1{font-size:clamp(38px,7vw,68px);line-height:1;margin:0 0 18px}.feature-page .lead,.script-reference .lead{font-size:21px;color:#d4dfdc;margin:0 0 20px}.feature-page section{border-top:1px solid #2a363a;padding-top:24px;margin-top:26px}.feature-page h2{font-size:28px;margin:0 0 12px}.feature-page li{margin:0 0 10px;color:#d2dcda}.hero{min-height:360px;background-size:cover;background-position:center;display:flex;align-items:flex-end}.hero .wrap{padding-top:90px;padding-bottom:46px}.hero h1{margin:0;font-size:clamp(38px,7vw,82px);line-height:.94}.meta{margin-top:16px;color:#cfd8d5}.layout{display:grid;grid-template-columns:minmax(0,1fr) 340px;gap:36px;padding-top:36px;padding-bottom:56px}.story{min-width:0}.story>p{font-size:19px;color:#d5dfdc}.gallery{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px;margin:30px 0}.gallery figure{margin:0;background:#182025}.gallery img{aspect-ratio:4/3;object-fit:cover}.gallery figcaption{padding:10px;color:#c7d0ce;font-size:14px}.panel{align-self:start}.map{width:100%;aspect-ratio:1;object-fit:cover;border:1px solid #2a363a}.stats,.parcels{margin-top:18px;background:#171e22;border:1px solid #263136;padding:18px}.stats h2,.parcels h2,.story h2{margin:0 0 14px}.stats dl{display:grid;grid-template-columns:1fr auto;gap:7px 16px;margin:0}.stats dt{color:#9facad}.stats dd{margin:0;font-weight:700}.parcels div{display:flex;justify-content:space-between;gap:12px;border-top:1px solid #263136;padding:9px 0}.parcels div:first-of-type{border-top:0}.parcels span{color:#aab6b8}.post{border-top:1px solid #2a363a;padding:22px 0}.post img{width:100%;max-height:360px;object-fit:cover;margin-bottom:14px}.post time{color:#9facad;font-size:13px}.post h3{margin:4px 0 8px;font-size:24px}.post p{color:#cbd5d2}.post-page{padding-top:36px;padding-bottom:60px;max-width:850px}.post.full h1{font-size:46px;line-height:1.05;margin:6px 0 22px}.post.full p{font-size:18px}.back{display:inline-block;margin-bottom:18px}.region-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:18px}.list{padding-top:42px;padding-bottom:60px}.region-card{background:#171e22;border:1px solid #263136;color:#e9efec}.region-card img{aspect-ratio:16/9;object-fit:cover}.region-card strong,.region-card span{display:block;padding:0 14px}.region-card strong{padding-top:13px;font-size:20px}.region-card span{padding-bottom:14px;color:#abb8b8}.empty code{word-break:break-all}.script-reference{padding-top:42px;padding-bottom:70px}.script-reference h1{font-size:clamp(38px,7vw,68px);line-height:1;margin:0 0 18px}.script-source{max-width:880px;color:#b8c6c3}.script-toc{border-top:1px solid #2a363a;margin-top:30px;padding-top:22px}.script-toc h2,.script-group h2{font-size:28px;margin:0 0 14px}.script-toc div{display:flex;flex-wrap:wrap;gap:10px}.script-toc a{background:#172229;border:1px solid #2c3a41;padding:9px 12px;color:#dce7e4}.script-toc span{color:#98b5bd}.script-group{border-top:1px solid #2a363a;margin-top:30px;padding-top:24px}.script-card{background:#161e22;border:1px solid #263238;padding:18px;margin:0 0 14px}.script-card-head{display:flex;align-items:flex-start;justify-content:space-between;gap:18px}.script-card h3{font-size:22px;margin:0}.script-card-head span{color:#9cb7bd;font-size:13px;text-align:right}.signature{margin:12px 0;color:#dbe7e4}.signature code,.script-card pre{background:#0c1114;border:1px solid #263238}.signature code{display:block;overflow:auto;padding:10px}.script-detail{margin:8px 0;color:#cbd6d3}.script-detail strong{color:#eef7f3}.script-card details{margin-top:12px}.script-card summary{cursor:pointer;color:#9bd3e6;font-weight:700}.script-card pre{overflow:auto;padding:12px;color:#dfeae7}.script-focus{border-top:1px solid #2a363a;margin-top:28px;padding-top:24px}@media(max-width:820px){.layout,.estate-stats{grid-template-columns:1fr}.hero{min-height:300px}.estate-hero{min-height:430px}.wrap{padding-left:16px;padding-right:16px}.script-card-head{display:block}.script-card-head span{text-align:left;display:block;margin-top:5px}}")
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
            Doc("Environment and time", "llGetEnvironment", "list llGetEnvironment(vector position, list rules)", "Rule/value pairs.", "Query supported EEP day, sky, water and environment rules at a position.", "None.", "Unsupported rules return SL-style invalid rule status where applicable."),
            Doc("Environment and time", "llReplaceEnvironment", "integer llReplaceEnvironment(vector position, string environment, integer track_no, integer day_length, integer day_offset)", "An ENV_* result code.", "Replace or clear parcel/region environment data using an inventory/environment asset id.", "Requires parcel or estate environment rights.", "Pass NULL_KEY or an empty string to clear where supported."),
            Doc("Environment and time", "llSetEnvironment", "integer llSetEnvironment(vector position, list parameters)", "An ENV_* result code.", "Attempt per-parameter environment overrides at a position.", "Requires parcel or estate environment rights.", "Returns ENV_INVALID_RULE for rules OpenSim cannot persist yet."),
            Doc("Environment and time", "llReplaceAgentEnvironment", "integer llReplaceAgentEnvironment(key agent, float transition, string environment)", "An ENV_* result code.", "Replace or clear a local agent environment.", "Requires a valid in-region agent and supported environment permissions.", "Useful for trusted estate experiences and ride effects."),
            Doc("Environment and time", "llSetAgentEnvironment", "integer llSetAgentEnvironment(key agent, float transition, list parameters)", "An ENV_* result code.", "Attempt per-agent environment parameter overrides.", "Requires a valid in-region agent and supported environment permissions.", "Returns ENV_INVALID_RULE for unsupported persistent overrides."),
            Doc("Estate and parcel", "llReturnObjectsByID", "integer llReturnObjectsByID(list object_ids)", "Number of objects returned.", "Return selected objects by UUID.", "Requires PERMISSION_RETURN_OBJECTS or simulator return rights.", "Uses normal simulator permission checks."),
            Doc("Estate and parcel", "llReturnObjectsByOwner", "integer llReturnObjectsByOwner(key owner, integer scope)", "Number of objects returned.", "Return objects owned by an avatar within the selected OBJECT_RETURN_* scope.", "Requires PERMISSION_RETURN_OBJECTS or simulator return rights.", "Use for estate cleanup panels."),
            Doc("Estate and parcel", "llSetGroundTexture", "integer llSetGroundTexture(list changes)", "TRUE on success.", "Set TERRAIN_DETAIL_* textures and TERRAIN_HEIGHT_RANGE_* blending heights.", "Script owner must be estate owner or estate manager.", "Estate manager checks now use the same owner-or-manager path as estate commands."),
            Doc("Estate and parcel", "llSetParcelForSale", "integer llSetParcelForSale(integer forSale, list options)", "A PARCEL_SALE_* result code.", "Mark the current parcel for sale or clear sale state using sale options.", "Requires parcel ownership or PERMISSION_PRIVILEGED_LAND_ACCESS where supported.", "Use for scripted land consoles."),
            Doc("Estate and parcel", "llParcelMediaCommandList", "void llParcelMediaCommandList(list commands)", "No return value.", "Set parcel media URL, texture, loop, auto-align, MIME type, description and size commands.", "Requires parcel media edit rights.", "PARCEL_MEDIA_COMMAND_LOOP_SET is supported."),
            Doc("Estate and parcel", "llParcelMediaQuery", "list llParcelMediaQuery(list commands)", "Requested media values.", "Read parcel media state for supported query fields.", "Requires parcel media visibility/edit context.", "Returns values in the requested command order."),
            Doc("Estate and parcel", "llManageEstateAccess", "integer llManageEstateAccess(integer action, string avatar)", "TRUE on successful mutation.", "Change estate access lists from trusted estate scripts.", "Script owner must be estate owner or estate manager.", "Mutations persist and notify estate info updates."),
            Doc("Inventory and ownership", "llGiveAgentInventory", "integer llGiveAgentInventory(key agent, string folderName, list inventory, list options)", "A TRANSFER_* result code.", "Deliver a folder of task inventory to an in-region agent.", "Items must satisfy copy/transfer checks.", "Use TRANSFER_DEST and TRANSFER_FLAGS options."),
            Doc("Inventory and ownership", "llTransferOwnership", "integer llTransferOwnership(key agent, integer flags, list options)", "A TRANSFER_* result code.", "Transfer the object or copy/take inventory delivery to another agent.", "Requires compatible object and inventory permissions.", "TRANSFER_FLAG_COPY and TRANSFER_FLAG_TAKE are supported."),
            Doc("Inventory and ownership", "llTransferLindenDollars", "key llTransferLindenDollars(key destination, integer amount)", "A transaction/query id.", "Start a scripted money transfer where the economy backend supports it.", "Requires debit permission and economy support.", "Use with normal money transaction handling."),
            Doc("Inventory and ownership", "llGetInventoryAcquireTime", "string llGetInventoryAcquireTime(string item)", "Acquire timestamp text.", "Read when an inventory item was acquired by the object.", "None.", "Returns an error if the item does not exist."),
            Doc("Inventory and ownership", "llGetInventoryDesc", "string llGetInventoryDesc(string item)", "Inventory item description.", "Read the description field for an object inventory item.", "None.", "Useful for data-driven object inventory."),
            Doc("Avatar and detection", "llDetectedRezzer", "key llDetectedRezzer(integer number)", "The rezzer object/avatar key, or NULL_KEY.", "Read provenance from detected data after sensor/collision/touch-style callbacks.", "None.", "The rezzer id now survives YEngine capture and restore."),
            Doc("Avatar and detection", "llGetAttachedListFiltered", "list llGetAttachedListFiltered(key agent, list options)", "Attachment object ids.", "Query attachments with FILTER_* options such as ATTACH_ANY_HUD and FILTER_FLAG_HUDS.", "HUD attachment visibility is limited to the script owner.", "Use for HUD-aware controllers without manual relay scripts."),
            Doc("Avatar and detection", "llSetAgentRot", "void llSetAgentRot(rotation rot, integer flags)", "No return value.", "Apply yaw rotation to the permissions-granted in-region avatar.", "Requires animation/control permissions for the avatar.", "Only yaw rotation is applied."),
            Doc("Avatar and detection", "llWorldPosToHUD", "vector llWorldPosToHUD(vector world_position)", "A HUD-space coordinate.", "Convert a world position to a HUD coordinate for indicators and pointing UI.", "Works from an attached HUD context.", "Useful for minimaps, markers and targeting displays."),
            Doc("Avatar and detection", "llMatchGroup", "integer llMatchGroup(key agent, list group_keys)", "TRUE when the agent active group matches.", "Check whether an in-region avatar has one of the supplied active groups.", "None.", "Avoids needing scripted llSameGroup relay prims."),
            Doc("Avatar and detection", "llIsFriend", "integer llIsFriend(key agent)", "TRUE when the simulator can treat the agent as a friend/same group.", "Use for compatibility with scripts that check friend-like access.", "None.", "Falls back to same-group behavior when friend service state is unavailable."),
            Doc("Materials and rendering", "llSetRenderMaterial", "void llSetRenderMaterial(string material, integer face)", "No return value.", "Apply a render material inventory item or material id to one face on the current prim.", "The material must resolve from object inventory or a valid asset id.", "Use an empty string to clear where supported."),
            Doc("Materials and rendering", "llSetLinkRenderMaterial", "void llSetLinkRenderMaterial(integer link, string material, integer face)", "No return value.", "Apply a render material to selected linked prims/faces.", "The material must resolve from object inventory or a valid asset id.", "For inventory names, the material item must be inside the object."),
            Doc("Materials and rendering", "llGetRenderMaterial", "string llGetRenderMaterial(integer face)", "The stored material id/name, or an empty string.", "Read the render material assigned to a face.", "None.", "Reads stored override state, not every unset property inside the material asset."),
            Doc("Materials and rendering", "llSetLinkGLTFOverrides", "void llSetLinkGLTFOverrides(integer link, integer face, list overrides)", "No return value.", "Set supported OVERRIDE_GLTF_* factors on selected linked prims/faces.", "Object edit rights.", "Supports base color/alpha, alpha mode, mask cutoff, double-sided, metallic, roughness and emissive factors."),
            Doc("Materials and rendering", "llIsLinkGLTFMaterial", "integer llIsLinkGLTFMaterial(integer link, integer face)", "TRUE when a face has GLTF material data.", "Check before applying or reading GLTF-specific overrides.", "None.", "Useful for mixed legacy/PBR builds."),
            Doc("Damage and combat", "llSetDamage", "void llSetDamage(float damage)", "No return value.", "Set object collision damage value.", "Object script control.", "Also available through PRIM_DAMAGE primitive params."),
            Doc("Damage and combat", "llDamage", "void llDamage(key target, float damage, integer damage_type)", "No return value.", "Apply supported avatar health damage and damage type.", "Requires simulator damage/combat support for meaningful effect.", "Uses OpenSim avatar health/death/teleport-home path."),
            Doc("Damage and combat", "llGetHealth", "float llGetHealth(string key)", "The target health value when known.", "Read avatar/object health compatibility state.", "None.", "Use OBJECT_HEALTH through llGetObjectDetails for object details workflows."),
            Doc("Damage and combat", "llDetectedDamage", "list llDetectedDamage(integer number)", "Detected damage metadata list.", "Compile Combat2-style scripts that inspect detected damage.", "Only meaningful when event metadata exists.", "Currently empty outside missing Combat2 event metadata."),
            Doc("Damage and combat", "llAdjustDamage", "void llAdjustDamage(float damage)", "No return value.", "Compile Combat2 on_damage scripts that try to adjust incoming damage.", "Only useful in an on_damage backend.", "Backend-limited: OpenSim does not yet provide Combat2 adjustment state."),
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
            Doc("Pathfinding compatibility", "llCreateCharacter", "void llCreateCharacter(list options)", "No return value; posts path_update failure.", "Compile SL pathfinding character scripts.", "No navmesh backend is present.", "Posts PU_FAILURE_NO_NAVMESH instead of faking movement."),
            Doc("Pathfinding compatibility", "llUpdateCharacter", "void llUpdateCharacter(list options)", "No return value; posts path_update failure.", "Compile scripts that update character options.", "No navmesh backend is present.", "Backend-limited compatibility surface."),
            Doc("Pathfinding compatibility", "llDeleteCharacter", "void llDeleteCharacter()", "No return value; posts path_update failure.", "Compile scripts that delete pathfinding characters.", "No navmesh backend is present.", "Backend-limited compatibility surface."),
            Doc("Pathfinding compatibility", "llExecCharacterCmd", "void llExecCharacterCmd(integer command, list options)", "No return value; posts path_update failure.", "Compile scripts that issue character commands.", "No navmesh backend is present.", "Backend-limited compatibility surface."),
            Doc("Pathfinding compatibility", "llNavigateTo", "void llNavigateTo(vector goal, list options)", "No return value; posts path_update failure.", "Compile scripts that request navigation.", "No navmesh backend is present.", "Posts PU_FAILURE_NO_NAVMESH."),
            Doc("Pathfinding compatibility", "llWanderWithin", "void llWanderWithin(vector origin, vector distance, list options)", "No return value; posts path_update failure.", "Compile scripts that request wandering behavior.", "No navmesh backend is present.", "Posts PU_FAILURE_NO_NAVMESH."),
            Doc("Pathfinding compatibility", "llPursue", "void llPursue(key target, list options)", "No return value; posts path_update failure.", "Compile scripts that request pursuit behavior.", "No navmesh backend is present.", "Posts PU_FAILURE_NO_NAVMESH."),
            Doc("Pathfinding compatibility", "llEvade", "void llEvade(key target, list options)", "No return value; posts path_update failure.", "Compile scripts that request evade behavior.", "No navmesh backend is present.", "Posts PU_FAILURE_NO_NAVMESH."),
            Doc("Pathfinding compatibility", "llFleeFrom", "void llFleeFrom(vector source, float distance, list options)", "No return value; posts path_update failure.", "Compile scripts that request flee behavior.", "No navmesh backend is present.", "Posts PU_FAILURE_NO_NAVMESH."),
            Doc("Pathfinding compatibility", "llGetStaticPath", "list llGetStaticPath(vector start, vector end, float radius, list parameters)", "A PU_FAILURE_NO_NAVMESH result list.", "Compile scripts that query static paths.", "No navmesh backend is present.", "Returns explicit failure rather than a fake path."),
            Doc("Pathfinding compatibility", "llGetClosestNavPoint", "vector llGetClosestNavPoint(vector point, list options)", "A vector, usually ZERO_VECTOR without navmesh.", "Compile scripts that query nav points.", "No navmesh backend is present.", "Backend-limited compatibility surface."),
            Doc("Misc compatibility", "llGenerateKey", "key llGenerateKey()", "A generated UUID.", "Generate a random UUID from script.", "None.", "Useful for local correlation ids."),
            Doc("Misc compatibility", "llGetAgentList", "list llGetAgentList(integer scope, list options)", "Agent keys.", "List agents matching scope/options.", "None.", "Use for region HUDs and access panels."),
            Doc("Misc compatibility", "llGetObjectLinkKey", "key llGetObjectLinkKey(key object_id, integer link)", "The child prim key.", "Resolve a link key on another object where visible to the simulator.", "None.", "Useful for object inspectors."),
            Doc("Misc compatibility", "llGetCameraAspect", "float llGetCameraAspect()", "Viewer camera aspect ratio.", "Read camera aspect after camera tracking permission.", "Requires PERMISSION_TRACK_CAMERA.", "Returns an error without permission."),
            Doc("Misc compatibility", "llGetCameraFOV", "float llGetCameraFOV()", "Viewer camera field of view.", "Read camera FOV after camera tracking permission.", "Requires PERMISSION_TRACK_CAMERA.", "Returns an error without permission."),
            Doc("Misc compatibility", "llSetAnimationOverride", "void llSetAnimationOverride(string anim_state, string animation)", "No return value.", "Set animation override state for the permissions-granted avatar.", "Requires animation override permission.", "Inventory animation names must resolve."),
            Doc("Misc compatibility", "llResetAnimationOverride", "void llResetAnimationOverride(string anim_state)", "No return value.", "Reset one animation override state.", "Requires animation override permission.", "Use an empty state to clear supported sets where accepted."),
            Doc("Misc compatibility", "llGetAnimationOverride", "string llGetAnimationOverride(string anim_state)", "Animation name or empty string.", "Read the active override for a state.", "Requires animation override permission.", "Useful in AO setup scripts."),
            Doc("Misc compatibility", "llSetSculptAnim", "void llSetSculptAnim(integer mode, integer sizex, integer sizey, integer start_frame, integer end_frame, float rate, integer texture_sync)", "No return value.", "Compile scripts that use SL sculpt-map animation calls.", "None.", "Backend-limited: OpenSim has no client-visible sculpt animation backend yet.")
        };

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
