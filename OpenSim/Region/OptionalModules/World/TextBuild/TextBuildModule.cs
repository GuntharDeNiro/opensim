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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.World.TextBuild
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "TextBuildModule")]
    public class TextBuildModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled;
        private int m_commandChannel;
        private bool m_estateManagerOnly;
        private int m_maxParts;
        private float m_spawnDistance;
        private bool m_aiEnabled;
        private string m_openAIEndpoint;
        private string m_openAIModel;
        private string m_openAIAPIKey;
        private int m_aiTimeoutMs;
        private float m_imageTerrainMinLandHeight;
        private float m_imageTerrainMaxLandHeight;
        private float m_imageTerrainSeaDepth;
        private bool m_imageTerrainFitLandToRegion;

        public string Name { get { return "Text Build Module"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["TextBuild"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            m_commandChannel = config.GetInt("CommandChannel", 0);
            m_estateManagerOnly = config.GetBoolean("EstateManagerOnly", true);
            m_maxParts = Math.Max(1, config.GetInt("MaxParts", 64));
            m_spawnDistance = Math.Max(1.0f, config.GetFloat("SpawnDistance", 4.0f));
            m_aiEnabled = config.GetBoolean("AIEnabled", false);
            m_openAIEndpoint = config.GetString("OpenAIEndpoint", "https://api.openai.com/v1/responses");
            m_openAIModel = config.GetString("OpenAIModel", "gpt-4.1-mini");
            m_openAIAPIKey = config.GetString("OpenAIAPIKey", string.Empty);
            if (string.IsNullOrWhiteSpace(m_openAIAPIKey))
                m_openAIAPIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
            m_aiTimeoutMs = Math.Max(500, config.GetInt("AITimeoutMs", 6000));
            m_imageTerrainMinLandHeight = Math.Max(0.1f, config.GetFloat("ImageTerrainMinLandHeight", 1.15f));
            m_imageTerrainMaxLandHeight = Math.Max(m_imageTerrainMinLandHeight, config.GetFloat("ImageTerrainMaxLandHeight", 30.0f));
            m_imageTerrainSeaDepth = Math.Max(0.1f, config.GetFloat("ImageTerrainSeaDepth", 5.0f));
            m_imageTerrainFitLandToRegion = config.GetBoolean("ImageTerrainFitLandToRegion", true);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            m_scene.EventManager.OnChatFromClient += OnChatFromClient;
            m_log.InfoFormat("[TEXT BUILD]: Enabled in region {0} on channel {1}", scene.RegionInfo.RegionName, m_commandChannel);
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_scene != null)
                m_scene.EventManager.OnChatFromClient -= OnChatFromClient;

            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (chat == null || chat.Sender == null)
                return;

            string request = chat.Message == null ? string.Empty : chat.Message.Trim();
            if (!IsBuildCommand(request))
                return;

            if (chat.Channel != m_commandChannel && chat.Channel != 0)
                return;

            request = NormalizeBuildRequest(request);

            IClientAPI client = chat.Sender;
            if (m_estateManagerOnly && !m_scene.Permissions.IsEstateManager(client.AgentId))
            {
                SendReply(client, "TextBuild: only estate managers can use automatic building here.");
                return;
            }

            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsChildAgent)
                return;

            TerrainRecipe terrainRecipe = ResolveTerrainRecipe(request);
            if (terrainRecipe != null)
            {
                ApplyTerrainRecipe(client, terrainRecipe);
                return;
            }

            BuildTemplate template = ResolveTemplate(request);
            if (template == null)
            {
                SendReply(client, "TextBuild: I can build car, boat, house, gazebo, portal, fountain, lamp, sofa, dock, table, flat terrain, tropical island, snowy mountains, or terrain from a cartography texture UUID.");
                return;
            }

            if (template.Parts.Count > m_maxParts)
            {
                SendReply(client, string.Format("TextBuild: template has {0} parts but MaxParts is {1}.", template.Parts.Count, m_maxParts));
                return;
            }

            Vector3 forward = Vector3.UnitX * sp.Rotation;
            forward.Z = 0f;
            if (forward.LengthSquared() < 0.001f)
                forward = Vector3.UnitX;
            forward.Normalize();

            Vector3 position = sp.AbsolutePosition + forward * m_spawnDistance;
            position.Z = Math.Max(position.Z, m_scene.GetGroundHeight(position.X, position.Y) + template.BaseHeight);

            if (!m_scene.Permissions.CanRezObject(template.Parts.Count, client.AgentId, position))
            {
                SendReply(client, "TextBuild: you cannot create objects at the target position.");
                return;
            }

            SceneObjectGroup group = CreateObject(client.AgentId, UUID.Zero, template, position, sp.Rotation);
            if (!m_scene.AddNewSceneObject(group, true))
            {
                SendReply(client, "TextBuild: object creation failed.");
                return;
            }

            group.InvalidateDeepEffectivePerms();
            group.ScheduleGroupForUpdate(PrimUpdateFlags.FullUpdatewithAnimMatOvr);
            SendReply(client, string.Format("TextBuild: built {0}.", template.Name));
        }

        private static bool IsBuildCommand(string request)
        {
            string lower = NormalizeBuildRequest(request).ToLower(CultureInfo.InvariantCulture);

            return lower.StartsWith("build ")
                || lower.StartsWith("create ")
                || lower.StartsWith("make ")
                || lower.StartsWith("costruisci ")
                || lower.StartsWith("costruiscimi ")
                || lower.StartsWith("crea ");
        }

        private static string NormalizeBuildRequest(string request)
        {
            request = request == null ? string.Empty : request.Trim();
            if (request.StartsWith("/"))
                request = request.Substring(1).TrimStart();

            return request;
        }

        private static BuildTemplate ResolveTemplate(string request)
        {
            string lower = request.ToLower(CultureInfo.InvariantCulture);

            if (lower.Contains("car") || lower.Contains("machine") || lower.Contains("macchina") || lower.Contains("auto"))
                return CreateCarTemplate();

            if (lower.Contains("boat") || lower.Contains("barca") || lower.Contains("yacht") || lower.Contains("sailboat") || lower.Contains("vela"))
                return CreateBoatTemplate();

            if (lower.Contains("house") || lower.Contains("home") || lower.Contains("casa"))
                return CreateHouseTemplate();

            if (lower.Contains("gazebo") || lower.Contains("pavilion") || lower.Contains("padiglione"))
                return CreateGazeboTemplate();

            if (lower.Contains("portal") || lower.Contains("portale") || lower.Contains("gate") || lower.Contains("teleport"))
                return CreatePortalTemplate();

            if (lower.Contains("tree") || lower.Contains("albero"))
                return CreateTreeTemplate();

            if (lower.Contains("fountain") || lower.Contains("fontana"))
                return CreateFountainTemplate();

            if (lower.Contains("lamp") || lower.Contains("streetlight") || lower.Contains("lampione") || lower.Contains("lanterna"))
                return CreateLampTemplate();

            if (lower.Contains("sofa") || lower.Contains("couch") || lower.Contains("divano"))
                return CreateSofaTemplate();

            if (lower.Contains("dock") || lower.Contains("pier") || lower.Contains("molo") || lower.Contains("pontile"))
                return CreateDockTemplate();

            if (lower.Contains("table") || lower.Contains("tavolo"))
                return CreateTableTemplate();

            return null;
        }

        private TerrainRecipe ResolveTerrainRecipe(string request)
        {
            string lower = request.ToLower(CultureInfo.InvariantCulture);

            if (TryResolveImageTerrainRecipe(request, lower, out TerrainRecipe imageRecipe))
                return imageRecipe;

            if (TryResolveAITerrainRecipe(request, lower, out TerrainRecipe aiRecipe))
                return aiRecipe;

            bool mentionsTerrain = lower.Contains("terrain")
                || lower.Contains("terreno")
                || lower.Contains("landscape")
                || lower.Contains("paesaggio")
                || lower.Contains("island")
                || lower.Contains("isola")
                || lower.Contains("tropical")
                || lower.Contains("tropicale")
                || lower.Contains("mountain")
                || lower.Contains("montagna")
                || lower.Contains("montagne")
                || lower.Contains("snow")
                || lower.Contains("neve")
                || lower.Contains("flat")
                || lower.Contains("piatto")
                || lower.Contains("erboso")
                || lower.Contains("grass")
                || lower.Contains("ring")
                || lower.Contains("anello")
                || lower.Contains("atoll")
                || lower.Contains("atollo")
                || lower.Contains("hole")
                || lower.Contains("buco")
                || lower.Contains("lagoon")
                || lower.Contains("laguna")
                || lower.Contains("volcano")
                || lower.Contains("vulcano")
                || lower.Contains("crater")
                || lower.Contains("cratere")
                || lower.Contains("archipelago")
                || lower.Contains("arcipelago")
                || lower.Contains("canyon")
                || lower.Contains("cartografia")
                || lower.Contains("satellite")
                || lower.Contains("sardegna")
                || lower.Contains("sardinia");

            if (!mentionsTerrain)
                return null;

            if (lower.Contains("ring") || lower.Contains("anello") || lower.Contains("atoll") || lower.Contains("atollo") || lower.Contains("hole") || lower.Contains("buco") || lower.Contains("lagoon") || lower.Contains("laguna"))
                return new TerrainRecipe("ring island", TerrainStyle.RingIsland, ExtractMeterValue(lower, 100f));

            if (lower.Contains("volcano") || lower.Contains("vulcano") || lower.Contains("crater") || lower.Contains("cratere"))
                return new TerrainRecipe("volcanic island", TerrainStyle.VolcanicIsland, ExtractMeterValue(lower, 62f));

            if (lower.Contains("archipelago") || lower.Contains("arcipelago"))
                return new TerrainRecipe("tropical archipelago", TerrainStyle.Archipelago);

            if (lower.Contains("canyon"))
                return new TerrainRecipe("canyon landscape", TerrainStyle.Canyon);

            if (lower.Contains("mountain") || lower.Contains("montagna") || lower.Contains("montagne") || lower.Contains("snow") || lower.Contains("neve"))
                return new TerrainRecipe("snowy mountains", TerrainStyle.SnowyMountains);

            if (lower.Contains("island") || lower.Contains("isola") || lower.Contains("tropical") || lower.Contains("tropicale"))
                return new TerrainRecipe("tropical island", TerrainStyle.TropicalIsland);

            if (lower.Contains("flat") || lower.Contains("piatto") || lower.Contains("erboso") || lower.Contains("grass") || lower.Contains("terrain") || lower.Contains("terreno"))
                return new TerrainRecipe("flat grassy terrain", TerrainStyle.FlatGrass);

            return null;
        }

        private static bool TryResolveImageTerrainRecipe(string request, string lower, out TerrainRecipe recipe)
        {
            recipe = null;

            if (!TryExtractUUID(request, out UUID textureID))
                return false;

            bool wantsImageTerrain =
                lower.Contains("cartografia")
                || lower.Contains("mappa reale")
                || lower.Contains("mappa")
                || lower.Contains("real map")
                || lower.Contains("map")
                || lower.Contains("world map")
                || lower.Contains("satellite")
                || lower.Contains("aerial")
                || lower.Contains("texture")
                || lower.Contains("uid")
                || lower.Contains("uuid")
                || lower.Contains("picture")
                || lower.Contains("immagine")
                || lower.Contains("heightmap")
                || lower.Contains("terrain")
                || lower.Contains("terreno")
                || lower.Contains("coastline")
                || lower.Contains("costa")
                || lower.Contains("sardegna")
                || lower.Contains("sardinia");

            if (!wantsImageTerrain)
                return false;

            string name = (lower.Contains("sardegna") || lower.Contains("sardinia"))
                ? "Sardinia image terrain"
                : "image-mapped terrain";

            recipe = new TerrainRecipe(name, TerrainStyle.ImageMap)
            {
                SourceTexture = textureID
            };
            return true;
        }

        private static bool TryExtractUUID(string text, out UUID id)
        {
            id = UUID.Zero;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            Match match = Regex.Match(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return match.Success && UUID.TryParse(match.Value, out id) && !id.IsZero();
        }

        private bool TryResolveAITerrainRecipe(string request, string lower, out TerrainRecipe recipe)
        {
            recipe = null;

            if (!m_aiEnabled || string.IsNullOrWhiteSpace(m_openAIEndpoint) || string.IsNullOrWhiteSpace(m_openAIAPIKey))
                return false;

            if (!CouldBeTerrainRequest(lower))
                return false;

            try
            {
                OSDMap result = RequestOpenAITerrainPlan(request);
                if (result == null)
                    return false;

                recipe = TerrainRecipeFromAIResult(result);
                if (recipe == null)
                    return false;

                m_log.InfoFormat("[TEXT BUILD]: AI terrain plan accepted: {0}", recipe.GetDescription());
                return true;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[TEXT BUILD]: OpenAI terrain plan failed, falling back to local parser: {0}", e.Message);
                return false;
            }
        }

        private OSDMap RequestOpenAITerrainPlan(string request)
        {
            string payload = CreateOpenAIRequestPayload(request);
            string responseBody = PostJsonToOpenAI(payload);
            OSDMap response = OSDParser.DeserializeJson(responseBody) as OSDMap;
            if (response == null)
                return null;

            string outputText = ExtractOpenAIOutputText(response);
            if (string.IsNullOrWhiteSpace(outputText))
                return null;

            return OSDParser.DeserializeJson(outputText) as OSDMap;
        }

        private string CreateOpenAIRequestPayload(string request)
        {
            string input = string.Format(
                CultureInfo.InvariantCulture,
                "Prompt: {0}\nRegion size: {1} x {2} meters\nWater height: {3:0.###}\n",
                request,
                m_scene.RegionInfo.RegionSizeX,
                m_scene.RegionInfo.RegionSizeY,
                m_scene.RegionInfo.RegionSettings.WaterHeight);

            StringBuilder json = new StringBuilder(4096);
            json.Append("{\"model\":").Append(JsonQuote(m_openAIModel));
            json.Append(",\"instructions\":").Append(JsonQuote(GetTerrainAIDeveloperInstructions()));
            json.Append(",\"input\":").Append(JsonQuote(input));
            json.Append(",\"max_output_tokens\":600");
            json.Append(",\"text\":{\"format\":{\"type\":\"json_schema\",\"name\":\"terrain_plan\",\"strict\":true,\"schema\":");
            json.Append(GetTerrainPlanJsonSchema());
            json.Append("}}}");
            return json.ToString();
        }

        private string PostJsonToOpenAI(string json)
        {
            using (HttpClient client = WebUtil.GetNewGlobalHttpClient(m_aiTimeoutMs))
            using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", m_openAIAPIKey);

                using (HttpResponseMessage response = client.PostAsync(m_openAIEndpoint, content).GetAwaiter().GetResult())
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                        throw new Exception(string.Format("OpenAI HTTP {0}: {1}", (int)response.StatusCode, TrimForLog(body, 500)));

                    return body;
                }
            }
        }

        private static string ExtractOpenAIOutputText(OSDMap response)
        {
            string outputText = GetOSDString(response, "output_text");
            if (!string.IsNullOrWhiteSpace(outputText))
                return outputText;

            OSDArray output = response.ContainsKey("output") ? response["output"] as OSDArray : null;
            if (output == null)
                return string.Empty;

            foreach (OSD outputItem in output)
            {
                OSDMap outputMap = outputItem as OSDMap;
                if (outputMap == null)
                    continue;

                OSDArray content = outputMap.ContainsKey("content") ? outputMap["content"] as OSDArray : null;
                if (content == null)
                    continue;

                foreach (OSD contentItem in content)
                {
                    OSDMap contentMap = contentItem as OSDMap;
                    if (contentMap == null)
                        continue;

                    string text = GetOSDString(contentMap, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            return string.Empty;
        }

        private static string GetTerrainAIDeveloperInstructions()
        {
            return "You translate OpenSim /build terrain requests into a safe TerrainPlan JSON object. "
                + "You do not generate a heightmap. Choose the closest supported style and modifier fields. "
                + "Supported styles: flat_grass, tropical_island, snowy_mountains, ring_island, volcanic_island, archipelago, canyon. "
                + "Use feature_meters for central lagoon, crater, hole, or similar central feature diameters. Use 0 otherwise. "
                + "Use flat_area when the prompt asks for a village, landing zone, plaza, building area, town, camp, harbor, or flat plateau. "
                + "Use slope_bias when one side should be steeper, taller, gentler, or more dramatic. "
                + "Use roughness below 1 for soft terrain and above 1 for rugged terrain. "
                + "Use height_scale above 1 for taller terrain and below 1 for lower terrain. "
                + "Use operations for custom requested geography. Coordinates are meters in the region, with x and y from 0 to region size. "
                + "Supported operations: raise_hill, lower_basin, flatten_area, cut_lake, carve_river, carve_bay, raise_ridge, crater, roughen. "
                + "Use x,y,radius for point features. Use x,y,x2,y2,width for linear features like rivers and ridges. "
                + "Use height for raised or flattened land, depth for water cuts, and strength for roughen or soft effects. "
                + "Return up to 16 strong operations that best match the user request. "
                + "If the user asks for black beaches, set beach_color to black. Only set terrain_textures UUIDs if the prompt explicitly provides UUIDs. "
                + "Leave terrain_textures values as empty strings when no UUIDs are supplied. Return JSON only.";
        }

        private static string GetTerrainPlanJsonSchema()
        {
            return "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{"
                + "\"style\":{\"type\":\"string\",\"enum\":[\"flat_grass\",\"tropical_island\",\"snowy_mountains\",\"ring_island\",\"volcanic_island\",\"archipelago\",\"canyon\"]},"
                + "\"feature_meters\":{\"type\":\"number\"},"
                + "\"name\":{\"type\":\"string\"},"
                + "\"height_scale\":{\"type\":\"number\"},"
                + "\"roughness\":{\"type\":\"number\"},"
                + "\"flat_area\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"side\":{\"type\":\"string\",\"enum\":[\"north\",\"south\",\"east\",\"west\",\"center\",\"\"]},\"size_meters\":{\"type\":\"number\"}},\"required\":[\"side\",\"size_meters\"]},"
                + "\"slope_bias\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"side\":{\"type\":\"string\",\"enum\":[\"north\",\"south\",\"east\",\"west\",\"center\",\"\"]},\"strength\":{\"type\":\"number\"}},\"required\":[\"side\",\"strength\"]},"
                + "\"beach_color\":{\"type\":\"string\",\"enum\":[\"\",\"black\",\"white\",\"gold\",\"sand\",\"rocky\"]},"
                + "\"terrain_textures\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"low\":{\"type\":\"string\"},\"mid\":{\"type\":\"string\"},\"high\":{\"type\":\"string\"},\"snow\":{\"type\":\"string\"}},\"required\":[\"low\",\"mid\",\"high\",\"snow\"]},"
                + "\"operations\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"type\":{\"type\":\"string\",\"enum\":[\"raise_hill\",\"lower_basin\",\"flatten_area\",\"cut_lake\",\"carve_river\",\"carve_bay\",\"raise_ridge\",\"crater\",\"roughen\"]},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"x2\":{\"type\":\"number\"},\"y2\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"depth\":{\"type\":\"number\"},\"strength\":{\"type\":\"number\"},\"noise_scale\":{\"type\":\"number\"}},\"required\":[\"type\",\"x\",\"y\",\"x2\",\"y2\",\"radius\",\"width\",\"height\",\"depth\",\"strength\",\"noise_scale\"]}}"
                + "},\"required\":[\"style\",\"feature_meters\",\"name\",\"height_scale\",\"roughness\",\"flat_area\",\"slope_bias\",\"beach_color\",\"terrain_textures\",\"operations\"]}";
        }

        private static string JsonQuote(string text)
        {
            if (text == null)
                return "\"\"";

            StringBuilder sb = new StringBuilder(text.Length + 8);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        private static string TrimForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength) + "...";
        }

        private static bool CouldBeTerrainRequest(string lower)
        {
            return lower.Contains("terrain")
                || lower.Contains("terreno")
                || lower.Contains("landscape")
                || lower.Contains("paesaggio")
                || lower.Contains("island")
                || lower.Contains("isola")
                || lower.Contains("mountain")
                || lower.Contains("montagna")
                || lower.Contains("montagne")
                || lower.Contains("snow")
                || lower.Contains("neve")
                || lower.Contains("flat")
                || lower.Contains("piatto")
                || lower.Contains("grass")
                || lower.Contains("erboso")
                || lower.Contains("ring")
                || lower.Contains("anello")
                || lower.Contains("atoll")
                || lower.Contains("atollo")
                || lower.Contains("hole")
                || lower.Contains("buco")
                || lower.Contains("lagoon")
                || lower.Contains("laguna")
                || lower.Contains("volcano")
                || lower.Contains("vulcano")
                || lower.Contains("crater")
                || lower.Contains("cratere")
                || lower.Contains("archipelago")
                || lower.Contains("arcipelago")
                || lower.Contains("canyon")
                || lower.Contains("valley")
                || lower.Contains("valle")
                || lower.Contains("lake")
                || lower.Contains("lago")
                || lower.Contains("beach")
                || lower.Contains("spiaggia")
                || lower.Contains("cartografia")
                || lower.Contains("satellite")
                || lower.Contains("sardegna")
                || lower.Contains("sardinia");
        }

        private static TerrainRecipe TerrainRecipeFromAIResult(OSDMap result)
        {
            string style = GetOSDString(result, "style");
            if (string.IsNullOrEmpty(style))
                style = GetOSDString(result, "terrain_style");

            style = NormalizeAIStyle(style);
            TerrainStyle terrainStyle;

            switch (style)
            {
                case "flat_grass":
                    terrainStyle = TerrainStyle.FlatGrass;
                    break;
                case "tropical_island":
                    terrainStyle = TerrainStyle.TropicalIsland;
                    break;
                case "snowy_mountains":
                    terrainStyle = TerrainStyle.SnowyMountains;
                    break;
                case "ring_island":
                    terrainStyle = TerrainStyle.RingIsland;
                    break;
                case "volcanic_island":
                    terrainStyle = TerrainStyle.VolcanicIsland;
                    break;
                case "archipelago":
                    terrainStyle = TerrainStyle.Archipelago;
                    break;
                case "canyon":
                    terrainStyle = TerrainStyle.Canyon;
                    break;
                default:
                    return null;
            }

            float meters = GetOSDFloat(result, "feature_meters", 0f);
            if (meters <= 0f)
                meters = GetOSDFloat(result, "center_hole_diameter", 0f);
            if (meters <= 0f)
                meters = GetOSDFloat(result, "hole_diameter", 0f);
            if (meters <= 0f)
                meters = GetOSDFloat(result, "lagoon_diameter", 0f);
            if (meters <= 0f)
                meters = GetOSDFloat(result, "crater_diameter", 0f);

            string name = GetOSDString(result, "name");
            if (string.IsNullOrEmpty(name))
                name = DefaultTerrainRecipeName(terrainStyle);

            if (meters > 0f)
                return ApplyAIRecipeModifiers(new TerrainRecipe(name, terrainStyle, Math.Max(5f, meters)), result);

            return ApplyAIRecipeModifiers(new TerrainRecipe(name, terrainStyle), result);
        }

        private static TerrainRecipe ApplyAIRecipeModifiers(TerrainRecipe recipe, OSDMap result)
        {
            recipe.HeightScale = Clamp(GetOSDFloat(result, "height_scale", 1f), 0.35f, 2.5f);
            recipe.Roughness = Clamp(GetOSDFloat(result, "roughness", 1f), 0f, 2.5f);
            recipe.BeachColor = GetOSDString(result, "beach_color");
            ApplyAITextureFields(recipe, result);
            ApplyAITerrainOperations(recipe, result);

            OSDMap flatArea = GetOSDMap(result, "flat_area");
            if (flatArea != null)
            {
                recipe.FlatAreaSide = NormalizeSide(GetOSDString(flatArea, "side"));
                recipe.FlatAreaMeters = Clamp(GetOSDFloat(flatArea, "size_meters", 45f), 8f, 220f);
            }

            OSDMap slopeBias = GetOSDMap(result, "slope_bias");
            if (slopeBias != null)
            {
                recipe.SlopeBiasSide = NormalizeSide(GetOSDString(slopeBias, "side"));
                recipe.SlopeBiasStrength = Clamp(GetOSDFloat(slopeBias, "strength", 0.65f), -1.5f, 1.5f);
            }

            return recipe;
        }

        private static void ApplyAITerrainOperations(TerrainRecipe recipe, OSDMap result)
        {
            OSDArray operations = result != null && result.ContainsKey("operations") ? result["operations"] as OSDArray : null;
            if (operations == null)
                return;

            int count = 0;
            foreach (OSD item in operations)
            {
                OSDMap map = item as OSDMap;
                if (map == null)
                    continue;

                string type = NormalizeOperationType(GetOSDString(map, "type"));
                if (string.IsNullOrEmpty(type))
                    continue;

                TerrainOperation operation = new TerrainOperation(type)
                {
                    X = GetOSDFloat(map, "x", 128f),
                    Y = GetOSDFloat(map, "y", 128f),
                    X2 = GetOSDFloat(map, "x2", 128f),
                    Y2 = GetOSDFloat(map, "y2", 128f),
                    Radius = Clamp(GetOSDFloat(map, "radius", 35f), 1f, 512f),
                    Width = Clamp(GetOSDFloat(map, "width", 12f), 1f, 256f),
                    Height = Clamp(GetOSDFloat(map, "height", 8f), -128f, 128f),
                    Depth = Clamp(GetOSDFloat(map, "depth", 3f), 0f, 128f),
                    Strength = Clamp(GetOSDFloat(map, "strength", 1f), -4f, 4f),
                    NoiseScale = Clamp(GetOSDFloat(map, "noise_scale", 0.055f), 0.005f, 0.25f)
                };

                recipe.Operations.Add(operation);
                count++;
                if (count >= 16)
                    break;
            }
        }

        private static string NormalizeOperationType(string type)
        {
            if (type == null)
                return string.Empty;

            type = type.Trim().ToLower(CultureInfo.InvariantCulture).Replace('-', '_').Replace(' ', '_');

            if (type == "hill" || type == "mountain" || type == "raise")
                return "raise_hill";
            if (type == "lake" || type == "pond")
                return "cut_lake";
            if (type == "river" || type == "stream")
                return "carve_river";
            if (type == "ridge" || type == "cliff")
                return "raise_ridge";
            if (type == "flat" || type == "plateau" || type == "village_area")
                return "flatten_area";
            if (type == "bay" || type == "harbor")
                return "carve_bay";

            if (type == "raise_hill" || type == "lower_basin" || type == "flatten_area" || type == "cut_lake"
                || type == "carve_river" || type == "carve_bay" || type == "raise_ridge" || type == "crater" || type == "roughen")
                return type;

            return string.Empty;
        }

        private static void ApplyAITextureFields(TerrainRecipe recipe, OSDMap result)
        {
            OSDMap textures = GetOSDMap(result, "terrain_textures");
            if (textures == null)
                textures = GetOSDMap(result, "textures");

            if (textures != null)
            {
                recipe.TerrainTexture1 = GetTextureUUID(textures, "low");
                if (recipe.TerrainTexture1.IsZero())
                    recipe.TerrainTexture1 = GetTextureUUID(textures, "beach");
                if (recipe.TerrainTexture1.IsZero())
                    recipe.TerrainTexture1 = GetTextureUUID(textures, "sand");
                if (recipe.TerrainTexture1.IsZero())
                    recipe.TerrainTexture1 = GetTextureUUID(textures, "texture1");

                recipe.TerrainTexture2 = GetTextureUUID(textures, "mid");
                if (recipe.TerrainTexture2.IsZero())
                    recipe.TerrainTexture2 = GetTextureUUID(textures, "grass");
                if (recipe.TerrainTexture2.IsZero())
                    recipe.TerrainTexture2 = GetTextureUUID(textures, "texture2");

                recipe.TerrainTexture3 = GetTextureUUID(textures, "high");
                if (recipe.TerrainTexture3.IsZero())
                    recipe.TerrainTexture3 = GetTextureUUID(textures, "rock");
                if (recipe.TerrainTexture3.IsZero())
                    recipe.TerrainTexture3 = GetTextureUUID(textures, "texture3");

                recipe.TerrainTexture4 = GetTextureUUID(textures, "snow");
                if (recipe.TerrainTexture4.IsZero())
                    recipe.TerrainTexture4 = GetTextureUUID(textures, "texture4");
            }

            if (recipe.TerrainTexture1.IsZero())
                recipe.TerrainTexture1 = GetTextureUUID(result, "terrain_texture_low");
            if (recipe.TerrainTexture1.IsZero())
                recipe.TerrainTexture1 = GetTextureUUID(result, "beach_texture");
        }

        private static UUID GetTextureUUID(OSDMap map, string key)
        {
            if (map == null || !map.ContainsKey(key))
                return UUID.Zero;

            if (UUID.TryParse(map[key].AsString(), out UUID textureID))
                return textureID;

            return UUID.Zero;
        }

        private static string NormalizeAIStyle(string style)
        {
            if (style == null)
                return string.Empty;

            style = style.Trim().ToLower(CultureInfo.InvariantCulture).Replace('-', '_').Replace(' ', '_');

            if (style == "flat" || style == "grass" || style == "grassy" || style == "flat_terrain")
                return "flat_grass";
            if (style == "island" || style == "tropical" || style == "tropical_isle")
                return "tropical_island";
            if (style == "mountains" || style == "snow" || style == "snowy" || style == "alpine")
                return "snowy_mountains";
            if (style == "ring" || style == "atoll" || style == "atollo" || style == "lagoon" || style == "ring_isle")
                return "ring_island";
            if (style == "volcano" || style == "volcanic" || style == "crater")
                return "volcanic_island";

            return style;
        }

        private static string DefaultTerrainRecipeName(TerrainStyle style)
        {
            switch (style)
            {
                case TerrainStyle.FlatGrass:
                    return "flat grassy terrain";
                case TerrainStyle.TropicalIsland:
                    return "tropical island";
                case TerrainStyle.SnowyMountains:
                    return "snowy mountains";
                case TerrainStyle.RingIsland:
                    return "ring island";
                case TerrainStyle.VolcanicIsland:
                    return "volcanic island";
                case TerrainStyle.Archipelago:
                    return "tropical archipelago";
                case TerrainStyle.Canyon:
                    return "canyon landscape";
                case TerrainStyle.ImageMap:
                    return "image-mapped terrain";
                default:
                    return "terrain";
            }
        }

        private static string GetOSDString(OSDMap map, string key)
        {
            if (map != null && map.ContainsKey(key))
                return map[key].AsString();

            return string.Empty;
        }

        private static float GetOSDFloat(OSDMap map, string key, float fallback)
        {
            if (map != null && map.ContainsKey(key))
                return (float)map[key].AsReal();

            return fallback;
        }

        private static OSDMap GetOSDMap(OSDMap map, string key)
        {
            if (map != null && map.ContainsKey(key))
                return map[key] as OSDMap;

            return null;
        }

        private static string NormalizeSide(string side)
        {
            if (side == null)
                return string.Empty;

            side = side.Trim().ToLower(CultureInfo.InvariantCulture);
            if (side == "n" || side == "nord")
                return "north";
            if (side == "s" || side == "sud")
                return "south";
            if (side == "e" || side == "est")
                return "east";
            if (side == "w" || side == "ovest")
                return "west";
            if (side == "middle" || side == "centre" || side == "centro")
                return "center";

            if (side == "north" || side == "south" || side == "east" || side == "west" || side == "center")
                return side;

            return string.Empty;
        }

        private static float ExtractMeterValue(string lower, float fallback)
        {
            Match match = Regex.Match(lower, @"(\d+(?:[\.,]\d+)?)\s*(?:m|meter|meters|metro|metri)\b");
            if (!match.Success)
                return fallback;

            string value = match.Groups[1].Value.Replace(',', '.');
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float meters))
                return Math.Max(5f, meters);

            return fallback;
        }

        private void ApplyTerrainRecipe(IClientAPI client, TerrainRecipe recipe)
        {
            if (m_scene.Heightmap == null)
            {
                SendReply(client, "TextBuild: terrain is not available in this region.");
                return;
            }

            int width = m_scene.Heightmap.Width;
            int height = m_scene.Heightmap.Height;

            if (width <= 0 || height <= 0)
            {
                SendReply(client, "TextBuild: terrain has invalid dimensions.");
                return;
            }

            if (recipe.Style == TerrainStyle.ImageMap)
            {
                recipe.ImageData = LoadImageTerrainData(recipe.SourceTexture);
                if (recipe.ImageData == null)
                {
                    SendReply(client, string.Format("TextBuild: could not decode cartography texture {0} as terrain source.", recipe.SourceTexture));
                    return;
                }
            }

            ApplyTerrainTextureHeights(recipe);

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    m_scene.Heightmap[x, y] = GenerateTerrainHeight(recipe, x, y, width, height);
                }
            }

            m_scene.Heightmap.GetTerrainData().TaintAllTerrain();
            if (m_scene.PhysicsScene != null)
                m_scene.PhysicsScene.SetTerrain(m_scene.Heightmap.GetFloatsSerialised());

            m_scene.SaveTerrain();
            m_scene.RegionInfo.RegionSettings.Save();
            m_scene.EventManager.TriggerTerrainTainted();
            m_scene.EventManager.TriggerTerrainCheckUpdates();
            m_scene.EventManager.TriggerTerrainUpdate();

            SendReply(client, string.Format("TextBuild: shaped terrain as {0}.", recipe.GetDescription()));
        }

        private ImageTerrainData LoadImageTerrainData(UUID textureID)
        {
            if (textureID.IsZero())
                return null;

            try
            {
                AssetBase asset = m_scene.AssetService.Get(textureID.ToString());
                if (asset == null || asset.Data == null || asset.Data.Length == 0)
                {
                    m_log.WarnFormat("[TEXT BUILD]: Cartography terrain texture {0} was not found.", textureID);
                    return null;
                }

                using (System.Drawing.Bitmap bitmap = DecodeTextureBitmap(textureID, asset.Data))
                {
                    if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                        return null;

                    return CreateImageTerrainData(textureID, bitmap, m_imageTerrainFitLandToRegion);
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[TEXT BUILD]: Failed to load cartography terrain texture {0}: {1}", textureID, e.Message);
                return null;
            }
        }

        private static System.Drawing.Bitmap DecodeTextureBitmap(UUID textureID, byte[] data)
        {
            ManagedImage managedImage = null;
            System.Drawing.Image image = null;

            try
            {
                if (OpenJPEG.DecodeToImage(data, out managedImage, out image) && image != null)
                {
                    using (image)
                        return new System.Drawing.Bitmap(image);
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[TEXT BUILD]: JPEG2000 decode failed for cartography texture {0}: {1}", textureID, e.Message);
            }
            finally
            {
                if (managedImage != null)
                    managedImage.Clear();
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(data))
                using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(stream))
                    return new System.Drawing.Bitmap(bitmap);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[TEXT BUILD]: Bitmap decode failed for cartography texture {0}: {1}", textureID, e.Message);
                return null;
            }
        }

        private static ImageTerrainData CreateImageTerrainData(UUID textureID, System.Drawing.Bitmap bitmap, bool fitLandToRegion)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            float[] land = new float[width * height];
            float[] relief = new float[width * height];

            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            int landPixels = 0;

            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    System.Drawing.Color color = bitmap.GetPixel(x, y);
                    int index = y * width + x;
                    land[index] = ClassifyMapLand(color);
                    relief[index] = ClassifyMapRelief(color);

                    if (land[index] > 0.56f)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                        landPixels++;
                    }
                }
            }

            if (!fitLandToRegion || landPixels < Math.Max(32, width * height / 2500))
            {
                minX = 0;
                minY = 0;
                maxX = width - 1;
                maxY = height - 1;
            }
            else
            {
                int padX = Math.Max(2, (int)((maxX - minX + 1) * 0.08f));
                int padY = Math.Max(2, (int)((maxY - minY + 1) * 0.08f));
                minX = Math.Max(0, minX - padX);
                minY = Math.Max(0, minY - padY);
                maxX = Math.Min(width - 1, maxX + padX);
                maxY = Math.Min(height - 1, maxY + padY);
            }

            return new ImageTerrainData(textureID, width, height, land, relief, minX, minY, maxX, maxY);
        }

        private static float ClassifyMapLand(System.Drawing.Color color)
        {
            float alpha = color.A / 255f;
            if (alpha < 0.05f)
                return 0f;

            float r = color.R / 255f;
            float g = color.G / 255f;
            float b = color.B / 255f;
            float luma = r * 0.2126f + g * 0.7152f + b * 0.0722f;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float saturation = max <= 0.001f ? 0f : (max - min) / max;

            float blueDominance = b - Math.Max(r, g) * 0.92f;
            float cyanDominance = Math.Min(g, b) - r * 1.22f;
            float water = Math.Max(
                SmoothStep(0.02f, 0.23f, blueDominance) * SmoothStep(0.18f, 0.50f, b),
                SmoothStep(0.04f, 0.30f, cyanDominance) * SmoothStep(0.18f, 0.52f, Math.Min(g, b)));

            if (b > 0.18f && b > r * 1.10f && b > g * 0.96f)
                water = Math.Max(water, 0.78f + saturation * 0.18f);

            if (luma < 0.045f)
                water *= 0.35f;

            return Clamp((1f - water) * alpha, 0f, 1f);
        }

        private static float ClassifyMapRelief(System.Drawing.Color color)
        {
            float r = color.R / 255f;
            float g = color.G / 255f;
            float b = color.B / 255f;
            float luma = r * 0.2126f + g * 0.7152f + b * 0.0722f;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float saturation = max <= 0.001f ? 0f : (max - min) / max;
            float warmRock = Math.Max(0f, r - b) * 0.55f + Math.Max(0f, (r + g) * 0.5f - b) * 0.30f;
            float brightRidge = SmoothStep(0.62f, 0.92f, luma) * (1f - saturation * 0.35f);
            float vegetation = Math.Max(0f, g - Math.Max(r, b) * 0.72f);

            return Clamp(0.18f + warmRock + brightRidge * 0.32f + saturation * 0.20f - vegetation * 0.18f, 0f, 1f);
        }

        private void ApplyTerrainTextureHeights(TerrainRecipe recipe)
        {
            RegionSettings settings = m_scene.RegionInfo.RegionSettings;
            TerrainStyle style = recipe.Style;

            if (!recipe.TerrainTexture1.IsZero())
                settings.TerrainTexture1 = recipe.TerrainTexture1;
            if (!recipe.TerrainTexture2.IsZero())
                settings.TerrainTexture2 = recipe.TerrainTexture2;
            if (!recipe.TerrainTexture3.IsZero())
                settings.TerrainTexture3 = recipe.TerrainTexture3;
            if (!recipe.TerrainTexture4.IsZero())
                settings.TerrainTexture4 = recipe.TerrainTexture4;

            if (style == TerrainStyle.SnowyMountains || style == TerrainStyle.VolcanicIsland || style == TerrainStyle.Canyon)
            {
                settings.Elevation1NW = settings.Elevation1NE = settings.Elevation1SE = settings.Elevation1SW = 24.0;
                settings.Elevation2NW = settings.Elevation2NE = settings.Elevation2SE = settings.Elevation2SW = 78.0;
                return;
            }

            if (style == TerrainStyle.TropicalIsland || style == TerrainStyle.RingIsland || style == TerrainStyle.Archipelago || style == TerrainStyle.ImageMap)
            {
                settings.Elevation1NW = settings.Elevation1NE = settings.Elevation1SE = settings.Elevation1SW = 20.5;
                settings.Elevation2NW = settings.Elevation2NE = settings.Elevation2SE = settings.Elevation2SW = 42.0;
                return;
            }

            settings.Elevation1NW = settings.Elevation1NE = settings.Elevation1SE = settings.Elevation1SW = 18.0;
            settings.Elevation2NW = settings.Elevation2NE = settings.Elevation2SE = settings.Elevation2SW = 35.0;
        }

        private float GenerateTerrainHeight(TerrainRecipe recipe, int x, int y, int width, int height)
        {
            float water = (float)m_scene.RegionInfo.RegionSettings.WaterHeight;
            float heightValue;

            if (recipe.Style == TerrainStyle.ImageMap)
                heightValue = GenerateImageTerrainHeight(recipe, x, y, width, height, water);
            else if (recipe.Style == TerrainStyle.SnowyMountains)
                heightValue = GenerateSnowyMountainHeight(x, y, width, height, water);
            else if (recipe.Style == TerrainStyle.TropicalIsland)
                heightValue = GenerateTropicalIslandHeight(x, y, width, height, water);
            else if (recipe.Style == TerrainStyle.RingIsland)
                heightValue = GenerateRingIslandHeight(x, y, width, height, water, recipe.MeterValue);
            else if (recipe.Style == TerrainStyle.VolcanicIsland)
                heightValue = GenerateVolcanicIslandHeight(x, y, width, height, water, recipe.MeterValue);
            else if (recipe.Style == TerrainStyle.Archipelago)
                heightValue = GenerateArchipelagoHeight(x, y, width, height, water);
            else if (recipe.Style == TerrainStyle.Canyon)
                heightValue = GenerateCanyonHeight(x, y, width, height, water);
            else
                heightValue = water + 1.6f + FractalNoise(x * 0.035f, y * 0.035f, 2001) * 0.18f;

            heightValue = ApplyTerrainRecipeModifiers(recipe, x, y, width, height, water, heightValue);
            heightValue = ApplyTerrainOperations(recipe, x, y, width, height, water, heightValue);

            return ClampTerrainHeight(heightValue);
        }

        private float GenerateImageTerrainHeight(TerrainRecipe recipe, int x, int y, int width, int height, float water)
        {
            ImageTerrainData image = recipe.ImageData;
            if (image == null)
                return water - m_imageTerrainSeaDepth;

            float u = width <= 1 ? 0.5f : (x + 0.5f) / width;
            float v = height <= 1 ? 0.5f : (y + 0.5f) / height;
            float land = image.SampleLand(u, v);
            float relief = image.SampleRelief(u, v);

            float coast = SmoothStep(0.36f, 0.72f, land);
            float openWater = 1f - SmoothStep(0.04f, 0.50f, land);
            float waterNoise = Math.Abs(FractalNoise(x * 0.022f, y * 0.022f, 23003));
            float seaHeight = water - m_imageTerrainSeaDepth * (0.72f + waterNoise * 0.28f) * openWater;

            float hillNoise = FractalNoise(x * 0.030f, y * 0.030f, 23029) * 2.0f
                + FractalNoise(x * 0.075f, y * 0.075f, 23041) * 0.80f;
            float landRise = m_imageTerrainMinLandHeight + (m_imageTerrainMaxLandHeight - m_imageTerrainMinLandHeight) * relief;
            float landHeight = water + landRise + hillNoise * Math.Max(0.15f, coast);

            float shoreBand = SmoothStep(0.42f, 0.62f, land) - SmoothStep(0.72f, 0.92f, land);
            if (shoreBand > 0f)
                landHeight = Lerp(landHeight, water + 0.65f + hillNoise * 0.10f, shoreBand * 0.62f);

            return ClampTerrainHeight(Lerp(seaHeight, landHeight, coast));
        }

        private static float ApplyTerrainRecipeModifiers(TerrainRecipe recipe, int x, int y, int width, int height, float water, float heightValue)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;

            if (recipe.HeightScale > 0f && Math.Abs(recipe.HeightScale - 1f) > 0.001f)
                heightValue = water + (heightValue - water) * recipe.HeightScale;

            if (recipe.Roughness > 0f && Math.Abs(recipe.Roughness - 1f) > 0.001f)
            {
                float extraRoughness = recipe.Roughness - 1f;
                heightValue += FractalNoise(x * 0.084f, y * 0.084f, 9917) * 3.2f * extraRoughness;
            }

            if (!string.IsNullOrEmpty(recipe.SlopeBiasSide) && Math.Abs(recipe.SlopeBiasStrength) > 0.001f)
            {
                float side = SideInfluence(recipe.SlopeBiasSide, nx, ny);
                float scale = 1f + recipe.SlopeBiasStrength * 0.55f * side;
                heightValue = water + (heightValue - water) * Math.Max(0.15f, scale);
            }

            if (!string.IsNullOrEmpty(recipe.FlatAreaSide) && recipe.FlatAreaMeters > 0f)
            {
                SideCenter(recipe.FlatAreaSide, out float cx, out float cy);
                float size = Clamp(recipe.FlatAreaMeters / Math.Min(width, height), 0.06f, 0.9f);
                float rx = recipe.FlatAreaSide == "north" || recipe.FlatAreaSide == "south" ? size * 1.45f : size * 0.82f;
                float ry = recipe.FlatAreaSide == "east" || recipe.FlatAreaSide == "west" ? size * 1.45f : size * 0.82f;
                if (recipe.FlatAreaSide == "center")
                {
                    rx = size;
                    ry = size;
                }

                float dx = Math.Abs(nx - cx) / Math.Max(0.001f, rx);
                float dy = Math.Abs(ny - cy) / Math.Max(0.001f, ry);
                float boxDistance = Math.Max(dx, dy);
                float influence = SmoothStep(1.22f, 0.58f, boxDistance);
                float padHeight = water + 2.4f;

                if (recipe.Style == TerrainStyle.VolcanicIsland || recipe.Style == TerrainStyle.SnowyMountains || recipe.Style == TerrainStyle.Canyon)
                    padHeight = water + 4.0f;

                float micro = FractalNoise(x * 0.05f, y * 0.05f, 1709) * 0.12f;
                heightValue = Lerp(heightValue, padHeight + micro, influence * 0.96f);
            }

            return ClampTerrainHeight(heightValue);
        }

        private static float ApplyTerrainOperations(TerrainRecipe recipe, int x, int y, int width, int height, float water, float heightValue)
        {
            if (recipe.Operations.Count == 0)
                return heightValue;

            float px = x + 0.5f;
            float py = y + 0.5f;

            foreach (TerrainOperation operation in recipe.Operations)
            {
                float ox = Clamp(operation.X, 0f, width);
                float oy = Clamp(operation.Y, 0f, height);
                float radius = Math.Max(1f, operation.Radius);
                float distance = Distance(px, py, ox, oy);
                float pointInfluence = SmoothStep(radius, radius * 0.08f, distance);

                if (operation.Type == "raise_hill")
                {
                    float heightAmount = operation.Height == 0f ? 12f : operation.Height;
                    heightValue += heightAmount * pointInfluence;
                }
                else if (operation.Type == "lower_basin")
                {
                    float depth = operation.Depth == 0f ? 5f : operation.Depth;
                    heightValue -= depth * pointInfluence;
                }
                else if (operation.Type == "flatten_area")
                {
                    float target = water + (operation.Height == 0f ? 3.0f : operation.Height);
                    float flattenInfluence = SmoothStep(radius, radius * 0.58f, distance);
                    float micro = FractalNoise(px * 0.055f, py * 0.055f, 7717) * 0.10f;
                    heightValue = Lerp(heightValue, target + micro, flattenInfluence * 0.96f);
                }
                else if (operation.Type == "cut_lake")
                {
                    float target = water - Math.Max(0.35f, operation.Depth == 0f ? 1.6f : operation.Depth);
                    float shore = SmoothStep(radius, radius * 0.78f, distance);
                    float center = SmoothStep(radius * 0.84f, radius * 0.20f, distance);
                    heightValue = Lerp(heightValue, water + 0.35f, shore * 0.34f);
                    heightValue = Lerp(heightValue, target, center);
                }
                else if (operation.Type == "carve_bay")
                {
                    float target = water - Math.Max(0.25f, operation.Depth == 0f ? 1.0f : operation.Depth);
                    float bay = SmoothStep(radius, radius * 0.12f, distance);
                    heightValue = Lerp(heightValue, target, bay * 0.92f);
                }
                else if (operation.Type == "crater")
                {
                    float craterRadius = Math.Max(2f, radius);
                    float ring = SmoothStep(craterRadius * 1.18f, craterRadius * 0.84f, distance)
                        - SmoothStep(craterRadius * 0.72f, craterRadius * 0.36f, distance);
                    float center = SmoothStep(craterRadius * 0.62f, craterRadius * 0.12f, distance);
                    float rimHeight = operation.Height == 0f ? 10f : operation.Height;
                    float basinTarget = water + 1.5f - Math.Max(0f, operation.Depth);
                    heightValue += rimHeight * ring;
                    heightValue = Lerp(heightValue, basinTarget, center * 0.88f);
                }
                else if (operation.Type == "roughen")
                {
                    float strength = operation.Strength == 0f ? 1f : operation.Strength;
                    heightValue += FractalNoise(px * operation.NoiseScale, py * operation.NoiseScale, 11939) * 5.0f * strength * pointInfluence;
                }
                else if (operation.Type == "carve_river")
                {
                    float lineDistance = DistanceToSegment(px, py, operation.X, operation.Y, operation.X2, operation.Y2);
                    float riverWidth = Math.Max(1f, operation.Width);
                    float river = SmoothStep(riverWidth * 2.2f, riverWidth * 0.42f, lineDistance);
                    float target = water - Math.Max(0.2f, operation.Depth == 0f ? 0.7f : operation.Depth);
                    heightValue = Lerp(heightValue, target, river * 0.94f);
                }
                else if (operation.Type == "raise_ridge")
                {
                    float lineDistance = DistanceToSegment(px, py, operation.X, operation.Y, operation.X2, operation.Y2);
                    float ridgeWidth = Math.Max(1f, operation.Width);
                    float ridge = SmoothStep(ridgeWidth * 2.4f, ridgeWidth * 0.22f, lineDistance);
                    float amount = operation.Height == 0f ? 14f : operation.Height;
                    heightValue += amount * ridge;
                }
            }

            return heightValue;
        }

        private static float SideInfluence(string side, float nx, float ny)
        {
            if (side == "north")
                return SmoothStep(-0.15f, 1f, ny);
            if (side == "south")
                return SmoothStep(0.15f, -1f, ny);
            if (side == "east")
                return SmoothStep(-0.15f, 1f, nx);
            if (side == "west")
                return SmoothStep(0.15f, -1f, nx);
            if (side == "center")
                return SmoothStep(0.9f, 0.0f, (float)Math.Sqrt(nx * nx + ny * ny));

            return 0f;
        }

        private static void SideCenter(string side, out float cx, out float cy)
        {
            cx = 0f;
            cy = 0f;

            if (side == "north")
                cy = 0.72f;
            else if (side == "south")
                cy = -0.72f;
            else if (side == "east")
                cx = 0.72f;
            else if (side == "west")
                cx = -0.72f;
        }

        private static float GenerateTropicalIslandHeight(int x, int y, int width, int height, float water)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;
            float distance = (float)Math.Sqrt(nx * nx + ny * ny);
            float island = SmoothStep(0.98f, 0.18f, distance);
            float beach = SmoothStep(0.92f, 0.72f, distance) - SmoothStep(0.72f, 0.48f, distance);
            float hills = FractalNoise(x * 0.026f, y * 0.026f, 5107) * 4.8f * island;
            float ridges = FractalNoise(x * 0.073f, y * 0.073f, 1109) * 1.4f * island;
            float heightValue = water - 2.2f + island * 18.5f + hills + ridges;

            if (beach > 0f)
                heightValue = Lerp(heightValue, water + 0.85f + ridges * 0.25f, beach * 0.72f);

            return ClampTerrainHeight(heightValue);
        }

        private static float GenerateRingIslandHeight(int x, int y, int width, int height, float water, float holeDiameterMeters)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;
            float distance = (float)Math.Sqrt(nx * nx + ny * ny);
            float holeRadius = Math.Min(0.78f, Math.Max(0.08f, holeDiameterMeters / Math.Min(width, height)));
            float outer = SmoothStep(1.04f, 0.72f, distance);
            float inner = SmoothStep(holeRadius * 0.82f, holeRadius * 1.18f, distance);
            float ring = outer * inner;
            float beachOuter = SmoothStep(1.02f, 0.88f, distance) * inner;
            float beachInner = SmoothStep(holeRadius * 1.35f, holeRadius * 1.05f, distance) * outer;
            float ridges = FractalNoise(x * 0.045f, y * 0.045f, 8111) * 2.2f * ring;
            float palmsGround = FractalNoise(x * 0.018f, y * 0.018f, 9127) * 3.0f * ring;
            float heightValue = water - 2.4f + ring * 10.5f + ridges + palmsGround;

            heightValue = Lerp(heightValue, water + 0.65f + ridges * 0.18f, Math.Max(beachOuter, beachInner) * 0.72f);

            if (distance < holeRadius)
                heightValue = Lerp(heightValue, water - 1.8f, SmoothStep(holeRadius, holeRadius * 0.55f, distance));

            return ClampTerrainHeight(heightValue);
        }

        private static float GenerateVolcanicIslandHeight(int x, int y, int width, int height, float water, float craterDiameterMeters)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;
            float distance = (float)Math.Sqrt(nx * nx + ny * ny);
            float island = SmoothStep(1.02f, 0.2f, distance);
            float craterRadius = Math.Min(0.55f, Math.Max(0.07f, craterDiameterMeters / Math.Min(width, height)));
            float cone = Math.Max(0f, 1f - distance / 0.82f);
            float crater = SmoothStep(craterRadius * 1.65f, craterRadius * 0.75f, distance);
            float lavaRoughness = Math.Abs(FractalNoise(x * 0.055f, y * 0.055f, 3331)) * 5.5f * island;
            float heightValue = water - 2.0f + island * 7.0f + cone * 52f + lavaRoughness;

            heightValue -= crater * 36f;
            if (distance < craterRadius)
                heightValue = Lerp(heightValue, water + 2.0f, SmoothStep(craterRadius, craterRadius * 0.35f, distance));

            return ClampTerrainHeight(heightValue);
        }

        private static float GenerateArchipelagoHeight(int x, int y, int width, int height, float water)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;
            float islands =
                Peak(nx, ny, -0.46f, 0.20f, 0.34f, 1.0f) +
                Peak(nx, ny, 0.18f, -0.28f, 0.30f, 1.0f) +
                Peak(nx, ny, 0.50f, 0.38f, 0.22f, 1.0f) +
                Peak(nx, ny, -0.05f, 0.58f, 0.20f, 0.85f) +
                Peak(nx, ny, -0.62f, -0.48f, 0.18f, 0.72f);
            islands = Math.Min(1.35f, islands);
            float land = SmoothStep(0.10f, 0.72f, islands);
            float beaches = SmoothStep(0.28f, 0.48f, islands) - SmoothStep(0.58f, 0.82f, islands);
            float hills = FractalNoise(x * 0.031f, y * 0.031f, 6407) * 5.2f * land;
            float heightValue = water - 2.6f + land * 15.0f + hills;

            if (beaches > 0f)
                heightValue = Lerp(heightValue, water + 0.75f, beaches * 0.65f);

            return ClampTerrainHeight(heightValue);
        }

        private static float GenerateCanyonHeight(int x, int y, int width, int height, float water)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;
            float riverCenter = 0.22f * (float)Math.Sin(nx * 4.2f) + 0.08f * (float)Math.Sin(nx * 11.0f);
            float riverDistance = Math.Abs(ny - riverCenter);
            float canyonCut = SmoothStep(0.36f, 0.04f, riverDistance);
            float rim = SmoothStep(0.12f, 0.32f, riverDistance);
            float strata = (float)Math.Sin((x + y * 0.42f) * 0.19f) * 1.4f;
            float rough = FractalNoise(x * 0.027f, y * 0.027f, 2609) * 6.5f;
            float heightValue = water + 28.0f + rim * 18.0f + rough + strata - canyonCut * 34.0f;

            if (riverDistance < 0.035f)
                heightValue = water + 0.35f;

            return ClampTerrainHeight(heightValue);
        }

        private static float GenerateSnowyMountainHeight(int x, int y, int width, int height, float water)
        {
            float nx = ((x + 0.5f) / width) * 2f - 1f;
            float ny = ((y + 0.5f) / height) * 2f - 1f;
            float edgeFade = SmoothStep(1.18f, 0.72f, Math.Max(Math.Abs(nx), Math.Abs(ny)));

            float peaks =
                Peak(nx, ny, -0.34f, 0.26f, 0.30f, 38f) +
                Peak(nx, ny, 0.18f, -0.12f, 0.22f, 48f) +
                Peak(nx, ny, 0.42f, 0.36f, 0.26f, 34f) +
                Peak(nx, ny, -0.05f, 0.52f, 0.20f, 28f);

            float foothills = FractalNoise(x * 0.018f, y * 0.018f, 7301) * 8.0f;
            float rough = Math.Abs(FractalNoise(x * 0.062f, y * 0.062f, 4103)) * 6.5f;
            float heightValue = water + 4.0f + (peaks + foothills + rough) * edgeFade;

            return ClampTerrainHeight(heightValue);
        }

        private static float Peak(float nx, float ny, float cx, float cy, float radius, float amplitude)
        {
            float dx = nx - cx;
            float dy = ny - cy;
            float d2 = dx * dx + dy * dy;
            return (float)Math.Exp(-d2 / (radius * radius)) * amplitude;
        }

        private static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static float DistanceToSegment(float px, float py, float x1, float y1, float x2, float y2)
        {
            float vx = x2 - x1;
            float vy = y2 - y1;
            float lengthSquared = vx * vx + vy * vy;

            if (lengthSquared < 0.001f)
                return Distance(px, py, x1, y1);

            float t = ((px - x1) * vx + (py - y1) * vy) / lengthSquared;
            t = Clamp(t, 0f, 1f);

            float cx = x1 + vx * t;
            float cy = y1 + vy * t;
            return Distance(px, py, cx, cy);
        }

        private static float FractalNoise(float x, float y, int seed)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float normalizer = 0f;

            for (int octave = 0; octave < 4; octave++)
            {
                total += SmoothNoise(x * frequency, y * frequency, seed + octave * 1013) * amplitude;
                normalizer += amplitude;
                amplitude *= 0.52f;
                frequency *= 2.03f;
            }

            return total / normalizer;
        }

        private static float SmoothNoise(float x, float y, int seed)
        {
            int ix = (int)Math.Floor(x);
            int iy = (int)Math.Floor(y);
            float fx = x - ix;
            float fy = y - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            float a = HashNoise(ix, iy, seed);
            float b = HashNoise(ix + 1, iy, seed);
            float c = HashNoise(ix, iy + 1, seed);
            float d = HashNoise(ix + 1, iy + 1, seed);

            return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }

        private static float HashNoise(int x, int y, int seed)
        {
            unchecked
            {
                int n = x * 374761393 + y * 668265263 + seed * 1442695041;
                n = (n ^ (n >> 13)) * 1274126177;
                n ^= n >> 16;
                return ((n & 0x7fffffff) / 1073741824f) - 1f;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = (value - edge0) / (edge1 - edge0);
            t = Math.Max(0f, Math.Min(1f, t));
            return t * t * (3f - 2f * t);
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * amount;
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static float ClampTerrainHeight(float value)
        {
            return Math.Max(Constants.MinTerrainHeightmap, Math.Min(Constants.MaxTerrainHeightmap, value));
        }

        private SceneObjectGroup CreateObject(UUID ownerId, UUID groupId, BuildTemplate template, Vector3 position, Quaternion avatarRotation)
        {
            Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, GetYaw(avatarRotation));
            BuildPart rootBuildPart = template.Parts[0];
            Vector3 rootPosition = position + rootBuildPart.Offset * yaw;
            SceneObjectPart root = CreatePart(ownerId, rootBuildPart, rootPosition, yaw, Vector3.Zero);
            root.Name = template.Name;

            SceneObjectGroup group = new SceneObjectGroup(root);
            group.SetGroup(groupId, null);

            for (int i = 1; i < template.Parts.Count; i++)
            {
                BuildPart buildPart = template.Parts[i];
                group.AddPart(CreatePart(ownerId, buildPart, rootPosition, yaw, buildPart.Offset - rootBuildPart.Offset));
            }

            return group;
        }

        private static SceneObjectPart CreatePart(UUID ownerId, BuildPart buildPart, Vector3 groupPosition, Quaternion groupRotation, Vector3 offset)
        {
            PrimitiveBaseShape shape;
            if (buildPart.Shape == BuildShape.Sphere)
                shape = PrimitiveBaseShape.CreateSphere();
            else if (buildPart.Shape == BuildShape.Cylinder)
                shape = PrimitiveBaseShape.CreateCylinder();
            else if (buildPart.Shape == BuildShape.Torus)
            {
                shape = PrimitiveBaseShape.CreateCylinder();
                shape.ProfileShape = ProfileShape.Circle;
                shape.PathCurve = (byte)Extrusion.Curve1;
                shape.PathScaleY = 150;
            }
            else if (buildPart.Shape == BuildShape.Prism)
            {
                shape = PrimitiveBaseShape.CreateBox();
                shape.ProfileShape = ProfileShape.EquilateralTriangle;
            }
            else
                shape = PrimitiveBaseShape.CreateBox();

            shape.Scale = buildPart.Scale;
            buildPart.ConfigureShape?.Invoke(shape);
            Primitive.TextureEntry textures = shape.Textures;
            textures.DefaultTexture.RGBA = buildPart.Color;
            shape.Textures = textures;

            SceneObjectPart part = new SceneObjectPart(ownerId, shape, groupPosition, groupRotation * buildPart.Rotation, offset);
            part.Name = buildPart.Name;
            part.Scale = buildPart.Scale;
            return part;
        }

        private void SendReply(IClientAPI client, string message)
        {
            client.SendChatMessage(
                message,
                (byte)ChatTypeEnum.Owner,
                Vector3.Zero,
                "TextBuild",
                UUID.Zero,
                UUID.Zero,
                (byte)ChatSourceType.Object,
                (byte)ChatAudibleLevel.Fully);
        }

        private static float GetYaw(Quaternion rotation)
        {
            Vector3 forward = Vector3.UnitX * rotation;
            return (float)Math.Atan2(forward.Y, forward.X);
        }

        private static BuildTemplate CreateCarTemplate()
        {
            Quaternion wheelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI * 0.5f);
            Quaternion windshieldRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.35f);
            return new BuildTemplate("textbuild sport car", 0.35f,
                Box("main body", new Vector3(0f, 0f, 0.45f), new Vector3(3.35f, 1.42f, 0.46f), new Color4(0.04f, 0.22f, 0.72f, 1f)),
                Box("front hood", new Vector3(1.15f, 0f, 0.72f), new Vector3(1.25f, 1.25f, 0.16f), windshieldRot, new Color4(0.05f, 0.28f, 0.88f, 1f)),
                Box("rear deck", new Vector3(-1.18f, 0f, 0.72f), new Vector3(1.05f, 1.25f, 0.16f), new Color4(0.03f, 0.18f, 0.62f, 1f)),
                Box("cabin glass", new Vector3(0.02f, 0f, 1.03f), new Vector3(1.1f, 1.05f, 0.46f), new Color4(0.09f, 0.15f, 0.19f, 0.88f)),
                Box("windshield", new Vector3(0.58f, 0f, 1.05f), new Vector3(0.08f, 1.0f, 0.52f), windshieldRot, new Color4(0.35f, 0.7f, 0.95f, 0.75f)),
                Box("front bumper", new Vector3(1.78f, 0f, 0.43f), new Vector3(0.18f, 1.36f, 0.2f), new Color4(0.02f, 0.02f, 0.025f, 1f)),
                Box("rear bumper", new Vector3(-1.78f, 0f, 0.43f), new Vector3(0.18f, 1.36f, 0.2f), new Color4(0.02f, 0.02f, 0.025f, 1f)),
                Box("left headlight", new Vector3(1.88f, 0.42f, 0.58f), new Vector3(0.05f, 0.32f, 0.12f), new Color4(1f, 0.92f, 0.55f, 1f)),
                Box("right headlight", new Vector3(1.88f, -0.42f, 0.58f), new Vector3(0.05f, 0.32f, 0.12f), new Color4(1f, 0.92f, 0.55f, 1f)),
                Cylinder("front left wheel", new Vector3(0.95f, 0.82f, 0.25f), new Vector3(0.48f, 0.48f, 0.3f), wheelRot, new Color4(0.015f, 0.015f, 0.018f, 1f)),
                Cylinder("front right wheel", new Vector3(0.95f, -0.82f, 0.25f), new Vector3(0.48f, 0.48f, 0.3f), wheelRot, new Color4(0.015f, 0.015f, 0.018f, 1f)),
                Cylinder("rear left wheel", new Vector3(-0.95f, 0.82f, 0.25f), new Vector3(0.48f, 0.48f, 0.3f), wheelRot, new Color4(0.015f, 0.015f, 0.018f, 1f)),
                Cylinder("rear right wheel", new Vector3(-0.95f, -0.82f, 0.25f), new Vector3(0.48f, 0.48f, 0.3f), wheelRot, new Color4(0.015f, 0.015f, 0.018f, 1f)),
                Cylinder("front left hub", new Vector3(0.95f, 0.99f, 0.25f), new Vector3(0.24f, 0.24f, 0.06f), wheelRot, new Color4(0.75f, 0.75f, 0.72f, 1f)),
                Cylinder("front right hub", new Vector3(0.95f, -0.99f, 0.25f), new Vector3(0.24f, 0.24f, 0.06f), wheelRot, new Color4(0.75f, 0.75f, 0.72f, 1f)),
                Cylinder("rear left hub", new Vector3(-0.95f, 0.99f, 0.25f), new Vector3(0.24f, 0.24f, 0.06f), wheelRot, new Color4(0.75f, 0.75f, 0.72f, 1f)),
                Cylinder("rear right hub", new Vector3(-0.95f, -0.99f, 0.25f), new Vector3(0.24f, 0.24f, 0.06f), wheelRot, new Color4(0.75f, 0.75f, 0.72f, 1f)));
        }

        private static BuildTemplate CreateBoatTemplate()
        {
            Quaternion bowRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.PI * 0.5f);
            Quaternion mastRot = Quaternion.Identity;
            return new BuildTemplate("textbuild small sailboat", 0.4f,
                Box("hull", new Vector3(0f, 0f, 0.42f), new Vector3(3.8f, 1.25f, 0.5f), new Color4(0.82f, 0.82f, 0.78f, 1f)),
                Prism("bow", new Vector3(2.05f, 0f, 0.43f), new Vector3(0.95f, 1.28f, 0.5f), bowRot, new Color4(0.78f, 0.78f, 0.74f, 1f)),
                Box("deck", new Vector3(-0.3f, 0f, 0.78f), new Vector3(2.8f, 0.9f, 0.12f), new Color4(0.62f, 0.44f, 0.24f, 1f)),
                Box("cabin", new Vector3(-0.65f, 0f, 1.0f), new Vector3(0.95f, 0.72f, 0.38f), new Color4(0.95f, 0.92f, 0.84f, 1f)),
                Cylinder("mast", new Vector3(0.45f, 0f, 1.95f), new Vector3(0.08f, 0.08f, 2.4f), mastRot, new Color4(0.54f, 0.38f, 0.18f, 1f)),
                Prism("main sail", new Vector3(0.72f, 0.08f, 1.85f), new Vector3(0.08f, 1.65f, 1.95f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.12f), new Color4(0.95f, 0.96f, 0.9f, 0.92f)),
                Prism("front sail", new Vector3(1.42f, -0.04f, 1.55f), new Vector3(0.07f, 1.2f, 1.45f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f), new Color4(0.88f, 0.92f, 0.94f, 0.88f)));
        }

        private static BuildTemplate CreateHouseTemplate()
        {
            return new BuildTemplate("textbuild cottage", 0.5f,
                Box("house body", new Vector3(0f, 0f, 1.05f), new Vector3(3.4f, 3.0f, 2.1f), new Color4(0.82f, 0.76f, 0.65f, 1f)),
                Prism("gable roof", new Vector3(0f, 0f, 2.42f), new Vector3(3.95f, 3.35f, 1.05f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Math.PI * 0.5f), new Color4(0.52f, 0.11f, 0.08f, 1f)),
                Box("front trim", new Vector3(1.74f, 0f, 2.0f), new Vector3(0.08f, 3.08f, 0.16f), new Color4(0.95f, 0.9f, 0.8f, 1f)),
                Box("door", new Vector3(1.75f, 0f, 0.72f), new Vector3(0.08f, 0.72f, 1.25f), new Color4(0.32f, 0.18f, 0.08f, 1f)),
                Cylinder("door knob", new Vector3(1.82f, -0.22f, 0.82f), new Vector3(0.09f, 0.09f, 0.05f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI * 0.5f), new Color4(0.95f, 0.72f, 0.22f, 1f)),
                Box("left window glass", new Vector3(1.76f, 0.95f, 1.32f), new Vector3(0.06f, 0.58f, 0.48f), new Color4(0.45f, 0.75f, 0.95f, 0.82f)),
                Box("right window glass", new Vector3(1.76f, -0.95f, 1.32f), new Vector3(0.06f, 0.58f, 0.48f), new Color4(0.45f, 0.75f, 0.95f, 0.82f)),
                Box("left window cross", new Vector3(1.8f, 0.95f, 1.32f), new Vector3(0.05f, 0.62f, 0.06f), new Color4(0.95f, 0.9f, 0.8f, 1f)),
                Box("right window cross", new Vector3(1.8f, -0.95f, 1.32f), new Vector3(0.05f, 0.62f, 0.06f), new Color4(0.95f, 0.9f, 0.8f, 1f)),
                Box("chimney", new Vector3(-0.85f, 0.72f, 3.02f), new Vector3(0.42f, 0.42f, 0.95f), new Color4(0.45f, 0.18f, 0.14f, 1f)));
        }

        private static BuildTemplate CreateGazeboTemplate()
        {
            return new BuildTemplate("textbuild gazebo", 0.2f,
                Cylinder("base", new Vector3(0f, 0f, 0.18f), new Vector3(3.3f, 3.3f, 0.22f), Quaternion.Identity, new Color4(0.56f, 0.43f, 0.28f, 1f), Hollow(0.48f)),
                Cylinder("roof", new Vector3(0f, 0f, 2.85f), new Vector3(3.65f, 3.65f, 0.45f), Quaternion.Identity, new Color4(0.22f, 0.34f, 0.38f, 1f), Taper(0.68f, 0.68f)),
                Cylinder("roof cap", new Vector3(0f, 0f, 3.18f), new Vector3(0.55f, 0.55f, 0.22f), Quaternion.Identity, new Color4(0.78f, 0.68f, 0.45f, 1f)),
                Cylinder("post north", new Vector3(0f, 1.35f, 1.48f), new Vector3(0.16f, 0.16f, 2.45f), Quaternion.Identity, new Color4(0.84f, 0.8f, 0.68f, 1f)),
                Cylinder("post south", new Vector3(0f, -1.35f, 1.48f), new Vector3(0.16f, 0.16f, 2.45f), Quaternion.Identity, new Color4(0.84f, 0.8f, 0.68f, 1f)),
                Cylinder("post east", new Vector3(1.35f, 0f, 1.48f), new Vector3(0.16f, 0.16f, 2.45f), Quaternion.Identity, new Color4(0.84f, 0.8f, 0.68f, 1f)),
                Cylinder("post west", new Vector3(-1.35f, 0f, 1.48f), new Vector3(0.16f, 0.16f, 2.45f), Quaternion.Identity, new Color4(0.84f, 0.8f, 0.68f, 1f)),
                Box("rail north", new Vector3(0f, 1.42f, 1.1f), new Vector3(2.25f, 0.12f, 0.16f), new Color4(0.84f, 0.8f, 0.68f, 1f)),
                Box("rail south", new Vector3(0f, -1.42f, 1.1f), new Vector3(2.25f, 0.12f, 0.16f), new Color4(0.84f, 0.8f, 0.68f, 1f)));
        }

        private static BuildTemplate CreatePortalTemplate()
        {
            Quaternion sideRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI * 0.5f);
            return new BuildTemplate("textbuild luminous portal", 0.15f,
                Cylinder("left pillar", new Vector3(0f, 0.95f, 1.55f), new Vector3(0.34f, 0.34f, 2.85f), Quaternion.Identity, new Color4(0.12f, 0.11f, 0.16f, 1f), Taper(0.25f, 0.25f)),
                Cylinder("right pillar", new Vector3(0f, -0.95f, 1.55f), new Vector3(0.34f, 0.34f, 2.85f), Quaternion.Identity, new Color4(0.12f, 0.11f, 0.16f, 1f), Taper(0.25f, 0.25f)),
                Torus("upper ring", new Vector3(0f, 0f, 2.92f), new Vector3(2.35f, 0.34f, 2.35f), sideRot, new Color4(0.08f, 0.22f, 0.34f, 1f)),
                Torus("inner glow", new Vector3(0.02f, 0f, 1.75f), new Vector3(1.65f, 0.08f, 2.45f), sideRot, new Color4(0.2f, 0.85f, 1f, 0.58f)),
                Sphere("core mist", new Vector3(0.04f, 0f, 1.72f), new Vector3(1.12f, 0.08f, 1.65f), new Color4(0.28f, 0.72f, 1f, 0.36f)),
                Cylinder("left crystal", new Vector3(0f, 1.22f, 3.08f), new Vector3(0.28f, 0.28f, 0.55f), Quaternion.Identity, new Color4(0.25f, 0.9f, 1f, 0.75f), Taper(0.85f, 0.85f)),
                Cylinder("right crystal", new Vector3(0f, -1.22f, 3.08f), new Vector3(0.28f, 0.28f, 0.55f), Quaternion.Identity, new Color4(0.25f, 0.9f, 1f, 0.75f), Taper(0.85f, 0.85f)),
                Cylinder("base ring", new Vector3(0f, 0f, 0.22f), new Vector3(2.45f, 2.45f, 0.22f), Quaternion.Identity, new Color4(0.08f, 0.08f, 0.11f, 1f), Hollow(0.62f)));
        }

        private static BuildTemplate CreateTreeTemplate()
        {
            return new BuildTemplate("textbuild tree", 0.45f,
                Cylinder("tree trunk", new Vector3(0f, 0f, 1.0f), new Vector3(0.45f, 0.45f, 2.0f), Quaternion.Identity, new Color4(0.32f, 0.17f, 0.07f, 1f)),
                Sphere("tree crown", new Vector3(0f, 0f, 2.45f), new Vector3(2.2f, 2.2f, 1.8f), new Color4(0.08f, 0.45f, 0.14f, 1f)),
                Sphere("tree crown left", new Vector3(0f, 0.7f, 2.0f), new Vector3(1.35f, 1.35f, 1.15f), new Color4(0.06f, 0.36f, 0.12f, 1f)),
                Sphere("tree crown right", new Vector3(0f, -0.7f, 2.0f), new Vector3(1.35f, 1.35f, 1.15f), new Color4(0.06f, 0.36f, 0.12f, 1f)));
        }

        private static BuildTemplate CreateFountainTemplate()
        {
            return new BuildTemplate("textbuild fountain", 0.15f,
                Cylinder("stone basin", new Vector3(0f, 0f, 0.28f), new Vector3(2.5f, 2.5f, 0.55f), Quaternion.Identity, new Color4(0.56f, 0.56f, 0.52f, 1f), Hollow(0.46f)),
                Cylinder("water surface", new Vector3(0f, 0f, 0.6f), new Vector3(2.12f, 2.12f, 0.08f), Quaternion.Identity, new Color4(0.18f, 0.58f, 0.9f, 0.75f), Hollow(0.18f)),
                Cylinder("center column", new Vector3(0f, 0f, 1.0f), new Vector3(0.38f, 0.38f, 1.15f), Quaternion.Identity, new Color4(0.62f, 0.62f, 0.58f, 1f), Taper(0.18f, 0.18f)),
                Sphere("upper bowl", new Vector3(0f, 0f, 1.58f), new Vector3(1.05f, 1.05f, 0.34f), new Color4(0.58f, 0.58f, 0.54f, 1f)),
                Cylinder("water jet", new Vector3(0f, 0f, 2.05f), new Vector3(0.12f, 0.12f, 0.85f), Quaternion.Identity, new Color4(0.45f, 0.82f, 1f, 0.62f)),
                Sphere("spray", new Vector3(0f, 0f, 2.52f), new Vector3(0.38f, 0.38f, 0.24f), new Color4(0.72f, 0.9f, 1f, 0.65f)));
        }

        private static BuildTemplate CreateLampTemplate()
        {
            return new BuildTemplate("textbuild street lamp", 0.15f,
                Cylinder("base", new Vector3(0f, 0f, 0.2f), new Vector3(0.55f, 0.55f, 0.28f), Quaternion.Identity, new Color4(0.12f, 0.12f, 0.12f, 1f)),
                Cylinder("pole", new Vector3(0f, 0f, 1.6f), new Vector3(0.14f, 0.14f, 2.7f), Quaternion.Identity, new Color4(0.08f, 0.08f, 0.08f, 1f)),
                Box("arm", new Vector3(0.45f, 0f, 2.85f), new Vector3(0.9f, 0.1f, 0.1f), new Color4(0.08f, 0.08f, 0.08f, 1f)),
                Sphere("lamp glow", new Vector3(0.95f, 0f, 2.62f), new Vector3(0.55f, 0.55f, 0.45f), new Color4(1f, 0.86f, 0.36f, 0.72f)),
                Cylinder("lamp cap", new Vector3(0.95f, 0f, 2.92f), new Vector3(0.68f, 0.68f, 0.15f), Quaternion.Identity, new Color4(0.06f, 0.06f, 0.06f, 1f)));
        }

        private static BuildTemplate CreateSofaTemplate()
        {
            return new BuildTemplate("textbuild sofa", 0.25f,
                Box("seat", new Vector3(0f, 0f, 0.58f), new Vector3(2.8f, 1.2f, 0.38f), new Color4(0.48f, 0.12f, 0.18f, 1f)),
                Box("back cushion", new Vector3(-0.1f, 0.6f, 1.02f), new Vector3(2.9f, 0.28f, 0.95f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.18f), new Color4(0.42f, 0.08f, 0.14f, 1f)),
                Box("left arm", new Vector3(1.52f, 0f, 0.82f), new Vector3(0.32f, 1.25f, 0.72f), new Color4(0.42f, 0.08f, 0.14f, 1f)),
                Box("right arm", new Vector3(-1.52f, 0f, 0.82f), new Vector3(0.32f, 1.25f, 0.72f), new Color4(0.42f, 0.08f, 0.14f, 1f)),
                Box("left pillow", new Vector3(0.72f, 0.04f, 0.86f), new Vector3(0.78f, 1.02f, 0.16f), new Color4(0.62f, 0.18f, 0.24f, 1f)),
                Box("right pillow", new Vector3(-0.72f, 0.04f, 0.86f), new Vector3(0.78f, 1.02f, 0.16f), new Color4(0.62f, 0.18f, 0.24f, 1f)),
                Cylinder("left front foot", new Vector3(1.05f, -0.45f, 0.18f), new Vector3(0.16f, 0.16f, 0.28f), Quaternion.Identity, new Color4(0.08f, 0.04f, 0.02f, 1f)),
                Cylinder("right front foot", new Vector3(-1.05f, -0.45f, 0.18f), new Vector3(0.16f, 0.16f, 0.28f), Quaternion.Identity, new Color4(0.08f, 0.04f, 0.02f, 1f)));
        }

        private static BuildTemplate CreateDockTemplate()
        {
            return new BuildTemplate("textbuild dock", 0.2f,
                Box("dock deck", new Vector3(0f, 0f, 0.35f), new Vector3(5.0f, 2.0f, 0.25f), new Color4(0.45f, 0.31f, 0.18f, 1f)),
                Cylinder("front left post", new Vector3(2.1f, 0.8f, -0.45f), new Vector3(0.22f, 0.22f, 1.6f), Quaternion.Identity, new Color4(0.28f, 0.18f, 0.1f, 1f)),
                Cylinder("front right post", new Vector3(2.1f, -0.8f, -0.45f), new Vector3(0.22f, 0.22f, 1.6f), Quaternion.Identity, new Color4(0.28f, 0.18f, 0.1f, 1f)),
                Cylinder("rear left post", new Vector3(-2.1f, 0.8f, -0.45f), new Vector3(0.22f, 0.22f, 1.6f), Quaternion.Identity, new Color4(0.28f, 0.18f, 0.1f, 1f)),
                Cylinder("rear right post", new Vector3(-2.1f, -0.8f, -0.45f), new Vector3(0.22f, 0.22f, 1.6f), Quaternion.Identity, new Color4(0.28f, 0.18f, 0.1f, 1f)));
        }

        private static BuildTemplate CreateTableTemplate()
        {
            return new BuildTemplate("textbuild table", 0.35f,
                Box("table top", new Vector3(0f, 0f, 1.0f), new Vector3(2.4f, 1.35f, 0.18f), new Color4(0.45f, 0.28f, 0.13f, 1f)),
                Box("table leg 1", new Vector3(0.9f, 0.45f, 0.5f), new Vector3(0.18f, 0.18f, 1.0f), new Color4(0.32f, 0.19f, 0.08f, 1f)),
                Box("table leg 2", new Vector3(0.9f, -0.45f, 0.5f), new Vector3(0.18f, 0.18f, 1.0f), new Color4(0.32f, 0.19f, 0.08f, 1f)),
                Box("table leg 3", new Vector3(-0.9f, 0.45f, 0.5f), new Vector3(0.18f, 0.18f, 1.0f), new Color4(0.32f, 0.19f, 0.08f, 1f)),
                Box("table leg 4", new Vector3(-0.9f, -0.45f, 0.5f), new Vector3(0.18f, 0.18f, 1.0f), new Color4(0.32f, 0.19f, 0.08f, 1f)));
        }

        private static BuildPart Box(string name, Vector3 offset, Vector3 scale, Color4 color)
        {
            return Box(name, offset, scale, Quaternion.Identity, color);
        }

        private static BuildPart Box(string name, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color)
        {
            return new BuildPart(name, BuildShape.Box, offset, scale, rotation, color, null);
        }

        private static BuildPart Sphere(string name, Vector3 offset, Vector3 scale, Color4 color)
        {
            return new BuildPart(name, BuildShape.Sphere, offset, scale, Quaternion.Identity, color, null);
        }

        private static BuildPart Prism(string name, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color)
        {
            return new BuildPart(name, BuildShape.Prism, offset, scale, rotation, color, null);
        }

        private static BuildPart Torus(string name, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color)
        {
            return new BuildPart(name, BuildShape.Torus, offset, scale, rotation, color, null);
        }

        private static BuildPart Cylinder(string name, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color)
        {
            return Cylinder(name, offset, scale, rotation, color, null);
        }

        private static BuildPart Cylinder(string name, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color, Action<PrimitiveBaseShape> configureShape)
        {
            return new BuildPart(name, BuildShape.Cylinder, offset, scale, rotation, color, configureShape);
        }

        private static Action<PrimitiveBaseShape> Hollow(float amount)
        {
            return shape =>
            {
                shape.HollowShape = HollowShape.Circle;
                shape.ProfileHollow = ClampProfileHollow(amount);
            };
        }

        private static Action<PrimitiveBaseShape> Taper(float x, float y)
        {
            return shape =>
            {
                shape.PathTaperX = ClampPathParam(x);
                shape.PathTaperY = ClampPathParam(y);
            };
        }

        private static ushort ClampProfileHollow(float value)
        {
            value = Math.Max(0f, Math.Min(0.95f, value));
            return (ushort)(value * 50000f);
        }

        private static sbyte ClampPathParam(float value)
        {
            value = Math.Max(-1f, Math.Min(1f, value));
            return (sbyte)(value * 100f);
        }

        private enum BuildShape
        {
            Box,
            Sphere,
            Cylinder,
            Prism,
            Torus
        }

        private enum TerrainStyle
        {
            FlatGrass,
            TropicalIsland,
            SnowyMountains,
            RingIsland,
            VolcanicIsland,
            Archipelago,
            Canyon,
            ImageMap
        }

        private class TerrainRecipe
        {
            public readonly string Name;
            public readonly TerrainStyle Style;
            public readonly float MeterValue;
            public UUID SourceTexture = UUID.Zero;
            public ImageTerrainData ImageData;
            public float HeightScale = 1f;
            public float Roughness = 1f;
            public string FlatAreaSide = string.Empty;
            public float FlatAreaMeters;
            public string SlopeBiasSide = string.Empty;
            public float SlopeBiasStrength;
            public string BeachColor = string.Empty;
            public UUID TerrainTexture1 = UUID.Zero;
            public UUID TerrainTexture2 = UUID.Zero;
            public UUID TerrainTexture3 = UUID.Zero;
            public UUID TerrainTexture4 = UUID.Zero;
            public readonly List<TerrainOperation> Operations = new List<TerrainOperation>();

            public TerrainRecipe(string name, TerrainStyle style)
                : this(name, style, 0f)
            {
            }

            public TerrainRecipe(string name, TerrainStyle style, float meterValue)
            {
                Name = name;
                Style = style;
                MeterValue = meterValue;
            }

            public string GetDescription()
            {
                List<string> details = new List<string>();

                if (MeterValue > 0f && (Style == TerrainStyle.RingIsland || Style == TerrainStyle.VolcanicIsland))
                    details.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.#}m center feature", MeterValue));

                if (!string.IsNullOrEmpty(FlatAreaSide) && FlatAreaMeters > 0f)
                    details.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.#}m flat area {1}", FlatAreaMeters, FlatAreaSide));

                if (!string.IsNullOrEmpty(SlopeBiasSide) && Math.Abs(SlopeBiasStrength) > 0.001f)
                    details.Add(string.Format(CultureInfo.InvariantCulture, "{0} slope bias {1:0.##}", SlopeBiasSide, SlopeBiasStrength));

                if (!string.IsNullOrEmpty(BeachColor))
                    details.Add(BeachColor + " beaches requested");

                if (!TerrainTexture1.IsZero())
                    details.Add("custom low terrain texture");

                if (!SourceTexture.IsZero())
                    details.Add("cartography texture " + SourceTexture);

                if (Operations.Count > 0)
                    details.Add(string.Format(CultureInfo.InvariantCulture, "{0} AI terrain operations", Operations.Count));

                if (details.Count > 0)
                    return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", Name, string.Join(", ", details.ToArray()));

                return Name;
            }
        }

        private class ImageTerrainData
        {
            public readonly UUID TextureID;
            public readonly int Width;
            public readonly int Height;
            public readonly float[] Land;
            public readonly float[] Relief;
            public readonly int MinX;
            public readonly int MinY;
            public readonly int MaxX;
            public readonly int MaxY;

            public ImageTerrainData(UUID textureID, int width, int height, float[] land, float[] relief, int minX, int minY, int maxX, int maxY)
            {
                TextureID = textureID;
                Width = width;
                Height = height;
                Land = land;
                Relief = relief;
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public float SampleLand(float u, float v)
            {
                return Sample(Land, u, v);
            }

            public float SampleRelief(float u, float v)
            {
                return Sample(Relief, u, v);
            }

            private float Sample(float[] values, float u, float v)
            {
                if (values == null || values.Length == 0 || Width <= 0 || Height <= 0)
                    return 0f;

                u = Clamp(u, 0f, 1f);
                v = Clamp(v, 0f, 1f);

                float x = MinX + u * Math.Max(0, MaxX - MinX);
                float y = MinY + (1f - v) * Math.Max(0, MaxY - MinY);
                int x0 = Math.Max(0, Math.Min(Width - 1, (int)Math.Floor(x)));
                int y0 = Math.Max(0, Math.Min(Height - 1, (int)Math.Floor(y)));
                int x1 = Math.Max(0, Math.Min(Width - 1, x0 + 1));
                int y1 = Math.Max(0, Math.Min(Height - 1, y0 + 1));
                float tx = x - x0;
                float ty = y - y0;

                float a = values[y0 * Width + x0];
                float b = values[y0 * Width + x1];
                float c = values[y1 * Width + x0];
                float d = values[y1 * Width + x1];

                return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
            }
        }

        private class TerrainOperation
        {
            public readonly string Type;
            public float X;
            public float Y;
            public float X2;
            public float Y2;
            public float Radius;
            public float Width;
            public float Height;
            public float Depth;
            public float Strength;
            public float NoiseScale;

            public TerrainOperation(string type)
            {
                Type = type;
            }
        }

        private class BuildTemplate
        {
            public readonly string Name;
            public readonly float BaseHeight;
            public readonly List<BuildPart> Parts;

            public BuildTemplate(string name, float baseHeight, params BuildPart[] parts)
            {
                Name = name;
                BaseHeight = baseHeight;
                Parts = new List<BuildPart>(parts);
            }
        }

        private class BuildPart
        {
            public readonly string Name;
            public readonly BuildShape Shape;
            public readonly Vector3 Offset;
            public readonly Vector3 Scale;
            public readonly Quaternion Rotation;
            public readonly Color4 Color;
            public readonly Action<PrimitiveBaseShape> ConfigureShape;

            public BuildPart(string name, BuildShape shape, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color, Action<PrimitiveBaseShape> configureShape)
            {
                Name = name;
                Shape = shape;
                Offset = offset;
                Scale = scale;
                Rotation = rotation;
                Color = color;
                ConfigureShape = configureShape;
            }
        }
    }
}
