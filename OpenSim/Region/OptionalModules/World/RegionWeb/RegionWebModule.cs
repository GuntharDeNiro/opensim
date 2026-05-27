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
                html.Append("<article><h3>").Append(Html(feature.Title)).Append("</h3><p>")
                    .Append(Html(feature.Body)).Append("</p></article>");
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
                content.Features.AddRange(configuredFeatures);
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
            Directory.CreateDirectory(mediaDir);

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
                + "Feature1 = \"High quality world map|Terrain textures, water depth shading, land detail, aerial tone mapping and sharper object overlays make map tiles more readable and less cartoon-like.\"\n"
                + "Feature2 = \"Mesh and sculpt aware map rendering|The map renderer can project mesh and sculpt geometry instead of reducing everything to huge flat prim boxes.\"\n"
                + "Feature3 = \"Cleaner water and alpha handling|Transparent or animated water overlays are filtered so wave planes do not become grey rectangles on the map, while solid large builds still render.\"\n"
                + "Feature4 = \"Background map generation|Initial map tiles can render after region registration so startup is not blocked by high quality image work.\"\n"
                + "Feature5 = \"Cooperative heavy rendering|Expensive map rendering yields during long object passes to avoid starving simulator watchdog and packet threads.\"\n"
                + "Feature6 = \"RegionWeb estate portal|Every region can have a public web page with photos, blog posts, map tile, parcels and live region statistics.\"\n"
                + "Feature7 = \"Weather module|Regions can run rain, storm, snow or sunny presets, with wind, clouds, lightning, thunder and automatic forecast cycling.\"\n"
                + "Feature8 = \"Text build tools|Estate builders can generate or manage terrain and building assistance from in-world text commands.\"\n"
                + "Feature9 = \"Group auto invite|Visitors can receive normal viewer group invitations on arrival without needing scripted invite objects.\"\n"
                + "Feature10 = \"Viewer polish|Simulator version branding and appearance fallback options reduce noisy viewer warnings and clouded-avatar edge cases.\"\n",
                new UTF8Encoding(false));
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

        private static void AddDefaultFeatures(List<FeatureItem> features)
        {
            features.Add(new FeatureItem
            {
                Title = "High quality world map",
                Body = "Terrain textures, water depth shading, land detail, aerial tone mapping and sharper object overlays make map tiles more readable."
            });
            features.Add(new FeatureItem
            {
                Title = "Mesh and sculpt aware rendering",
                Body = "Map tiles can draw mesh and sculpt geometry instead of treating complex content as oversized flat prims."
            });
            features.Add(new FeatureItem
            {
                Title = "Cleaner water overlays",
                Body = "Transparent wave planes are filtered from the map while solid docks, boats and large builds still render."
            });
            features.Add(new FeatureItem
            {
                Title = "RegionWeb pages",
                Body = "Each region gets photos, blog posts, a live map, parcel summaries and useful visitor statistics."
            });
            features.Add(new FeatureItem
            {
                Title = "Weather and visitor polish",
                Body = "Optional weather presets, group auto-invites, viewer version branding and appearance fallback improve the daily experience."
            });
        }

        private static string Stat(string label, string value)
        {
            return "<dt>" + Html(label) + "</dt><dd>" + Html(value) + "</dd>";
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
                .Append("body{margin:0;background:#101417;color:#e9efec;font:16px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}a{color:#9bd3e6;text-decoration:none}img{max-width:100%;display:block}.wrap{max-width:1180px;margin:0 auto;padding:0 24px}.estate-hero{min-height:520px;background-size:cover;background-position:center;display:flex;align-items:flex-end}.estate-hero-plain{background:linear-gradient(135deg,#11252b,#1e2927 52%,#3a3526)}.estate-hero .wrap{padding-top:110px;padding-bottom:72px}.estate-hero p{max-width:760px;color:#d9e5e1;font-size:19px}.estate-hero>div>p:first-child,.hero p{margin:0 0 10px;color:#b9d8d3;text-transform:uppercase;font-size:13px;letter-spacing:.08em}.estate-hero h1{max-width:900px;margin:0;font-size:clamp(44px,8vw,96px);line-height:.92}.estate-actions{display:flex;flex-wrap:wrap;gap:12px;margin-top:28px}.estate-actions a{background:#d7e4df;color:#101417;padding:10px 15px;font-weight:700}.estate-actions a+a{background:#223239;color:#dbe7e4}.estate-stats{display:grid;grid-template-columns:repeat(5,1fr);gap:1px;margin-top:28px;background:#2a363a}.estate-stats div{background:#171e22;padding:18px}.estate-stats strong{display:block;font-size:30px}.estate-stats span{color:#aebbb9}.feature-section{padding-top:48px}.feature-section h2,.list h2{font-size:34px;margin:0 0 20px}.feature-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px}.feature-grid article{background:#171e22;border:1px solid #263136;padding:18px}.feature-grid h3{margin:0 0 8px;font-size:21px}.feature-grid p{margin:0;color:#c7d2cf}.hero{min-height:360px;background-size:cover;background-position:center;display:flex;align-items:flex-end}.hero .wrap{padding-top:90px;padding-bottom:46px}.hero h1{margin:0;font-size:clamp(38px,7vw,82px);line-height:.94}.meta{margin-top:16px;color:#cfd8d5}.layout{display:grid;grid-template-columns:minmax(0,1fr) 340px;gap:36px;padding-top:36px;padding-bottom:56px}.story{min-width:0}.story>p{font-size:19px;color:#d5dfdc}.gallery{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px;margin:30px 0}.gallery figure{margin:0;background:#182025}.gallery img{aspect-ratio:4/3;object-fit:cover}.gallery figcaption{padding:10px;color:#c7d0ce;font-size:14px}.panel{align-self:start}.map{width:100%;aspect-ratio:1;object-fit:cover;border:1px solid #2a363a}.stats,.parcels{margin-top:18px;background:#171e22;border:1px solid #263136;padding:18px}.stats h2,.parcels h2,.story h2{margin:0 0 14px}.stats dl{display:grid;grid-template-columns:1fr auto;gap:7px 16px;margin:0}.stats dt{color:#9facad}.stats dd{margin:0;font-weight:700}.parcels div{display:flex;justify-content:space-between;gap:12px;border-top:1px solid #263136;padding:9px 0}.parcels div:first-of-type{border-top:0}.parcels span{color:#aab6b8}.post{border-top:1px solid #2a363a;padding:22px 0}.post img{width:100%;max-height:360px;object-fit:cover;margin-bottom:14px}.post time{color:#9facad;font-size:13px}.post h3{margin:4px 0 8px;font-size:24px}.post p{color:#cbd5d2}.post-page{padding-top:36px;padding-bottom:60px;max-width:850px}.post.full h1{font-size:46px;line-height:1.05;margin:6px 0 22px}.post.full p{font-size:18px}.back{display:inline-block;margin-bottom:18px}.region-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:18px}.list{padding-top:42px;padding-bottom:60px}.region-card{background:#171e22;border:1px solid #263136;color:#e9efec}.region-card img{aspect-ratio:16/9;object-fit:cover}.region-card strong,.region-card span{display:block;padding:0 14px}.region-card strong{padding-top:13px;font-size:20px}.region-card span{padding-bottom:14px;color:#abb8b8}.empty code{word-break:break-all}@media(max-width:820px){.layout,.estate-stats{grid-template-columns:1fr}.hero{min-height:300px}.estate-hero{min-height:430px}.wrap{padding-left:16px;padding-right:16px}}")
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
