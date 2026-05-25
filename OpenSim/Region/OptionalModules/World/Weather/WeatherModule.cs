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
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.World.Weather
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WeatherModule")]
    public class WeatherModule : INonSharedRegionModule
    {
        private enum WeatherKind
        {
            Clear,
            Rain,
            Storm,
            Snow
        }

        private const uint ParticleFlags =
            1u |    // PSYS_PART_INTERP_COLOR_MASK
            2u |    // PSYS_PART_INTERP_SCALE_MASK
            8u |    // PSYS_PART_WIND_MASK
            256u;   // PSYS_PART_EMISSIVE_MASK

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object m_sync = new object();
        private readonly List<SceneObjectGroup> m_emitters = new List<SceneObjectGroup>();
        private readonly Random m_random = new Random();

        private Scene m_scene;
        private bool m_enabled;
        private bool m_estateManagerOnly;
        private int m_commandChannel;
        private int m_emitterGrid;
        private float m_emitterHeight;
        private float m_intensity;
        private WeatherKind m_currentWeather = WeatherKind.Clear;

        public string Name { get { return "Weather Module"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["Weather"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            m_commandChannel = config.GetInt("CommandChannel", 89);
            m_estateManagerOnly = config.GetBoolean("EstateManagerOnly", true);
            m_emitterGrid = Math.Max(1, config.GetInt("EmitterGrid", 8));
            m_emitterHeight = Math.Max(4f, config.GetFloat("EmitterHeight", 18f));
            m_intensity = Clamp(config.GetFloat("Intensity", 1f), 0.1f, 4f);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            m_scene.EventManager.OnChatFromClient += OnChatFromClient;
            m_log.InfoFormat(
                "[WEATHER]: Enabled in region {0} on channel {1}",
                scene.RegionInfo.RegionName,
                m_commandChannel);
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_scene != null)
                m_scene.EventManager.OnChatFromClient -= OnChatFromClient;

            ClearWeather(false);
            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
            ClearWeather(false);
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (chat == null || chat.Sender == null || chat.Channel != m_commandChannel)
                return;

            string request = chat.Message == null ? string.Empty : chat.Message.Trim();
            if (!IsWeatherCommand(request))
                return;

            IClientAPI client = chat.Sender;
            if (m_estateManagerOnly && !m_scene.Permissions.IsEstateManager(client.AgentId))
            {
                SendReply(client, "Weather: only estate managers can change weather here.");
                return;
            }

            if (IsStatusCommand(request))
            {
                SendStatus(client);
                return;
            }

            if (!TryResolveWeather(request, out WeatherKind weather))
            {
                SendReply(client, "Weather: use rain, storm, snow, clear, or status.");
                return;
            }

            if (weather == WeatherKind.Clear)
            {
                ClearWeather(true);
                SendReply(client, "Weather: clear.");
                return;
            }

            if (ApplyWeather(weather, client.AgentId))
                SendReply(client, string.Format("Weather: {0} started.", WeatherName(weather)));
            else
                SendReply(client, "Weather: could not create emitters.");
        }

        private bool ApplyWeather(WeatherKind weather, UUID ownerId)
        {
            ClearWeather(false);

            if (m_scene == null)
                return false;

            List<SceneObjectGroup> created = new List<SceneObjectGroup>();
            int sizeX = Math.Max(1, (int)m_scene.RegionInfo.RegionSizeX);
            int sizeY = Math.Max(1, (int)m_scene.RegionInfo.RegionSizeY);
            float spacingX = sizeX / (float)m_emitterGrid;
            float spacingY = sizeY / (float)m_emitterGrid;
            float radius = Math.Max(spacingX, spacingY) * 0.62f;

            for (int x = 0; x < m_emitterGrid; x++)
            {
                for (int y = 0; y < m_emitterGrid; y++)
                {
                    float posX = JitteredCellPosition(x, spacingX, sizeX);
                    float posY = JitteredCellPosition(y, spacingY, sizeY);
                    float ground = m_scene.GetGroundHeight(posX, posY);
                    Vector3 position = new Vector3(posX, posY, ground + JitterHeight());

                    SceneObjectGroup emitter = CreateEmitter(ownerId, weather, position, radius);
                    if (!m_scene.AddNewSceneObject(emitter, false))
                    {
                        DeleteEmitters(created);
                        return false;
                    }

                    emitter.RootPart.SendFullUpdateToAllClients();
                    emitter.ScheduleGroupForUpdate(PrimUpdateFlags.FullUpdatewithAnimMatOvr);
                    created.Add(emitter);
                }
            }

            lock (m_sync)
            {
                m_emitters.AddRange(created);
                m_currentWeather = weather;
            }

            m_log.InfoFormat(
                "[WEATHER]: Started {0} in {1} with {2} emitters",
                WeatherName(weather),
                m_scene.RegionInfo.RegionName,
                created.Count);

            return true;
        }

        private SceneObjectGroup CreateEmitter(UUID ownerId, WeatherKind weather, Vector3 position, float radius)
        {
            PrimitiveBaseShape shape = PrimitiveBaseShape.CreateSphere();
            shape.Scale = new Vector3(0.1f, 0.1f, 0.1f);
            Primitive.TextureEntry textures = shape.Textures;
            textures.DefaultTexture.RGBA = new Color4(1f, 1f, 1f, 0f);
            shape.Textures = textures;

            SceneObjectPart root = new SceneObjectPart(ownerId, shape, position, Quaternion.Identity, Vector3.Zero);
            root.Name = "weather " + WeatherName(weather) + " emitter";
            root.Scale = shape.Scale;
            root.AddFlag(PrimFlags.Phantom);
            root.AddNewParticleSystem(CreateParticleSystem(weather, radius), false);

            SceneObjectGroup group = new SceneObjectGroup(root);
            group.SetGroup(UUID.Zero, null);
            return group;
        }

        private Primitive.ParticleSystem CreateParticleSystem(WeatherKind weather, float radius)
        {
            Primitive.ParticleSystem particles = new Primitive.ParticleSystem
            {
                CRC = 1,
                PartDataFlags = (Primitive.ParticleSystem.ParticleDataFlags)ParticleFlags,
                Pattern = (Primitive.ParticleSystem.SourcePattern)2, // PSYS_SRC_PATTERN_EXPLODE
                Texture = Util.BLANK_TEXTURE_UUID,
                BurstRadius = radius,
                MaxAge = 0f,
                InnerAngle = 0f,
                OuterAngle = 0f,
                BlendFuncSource = 7, // PSYS_PART_BF_SOURCE_ALPHA
                BlendFuncDest = 9    // PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA
            };

            if (weather == WeatherKind.Snow)
            {
                particles.PartStartColor = new Color4(1f, 1f, 1f, 0.78f);
                particles.PartEndColor = new Color4(0.95f, 0.98f, 1f, 0.08f);
                particles.PartStartScaleX = 0.12f;
                particles.PartStartScaleY = 0.12f;
                particles.PartEndScaleX = 0.24f;
                particles.PartEndScaleY = 0.24f;
                particles.BurstSpeedMin = 0.05f;
                particles.BurstSpeedMax = 0.22f;
                particles.BurstRate = 0.11f;
                particles.PartMaxAge = 12.0f;
                particles.BurstPartCount = (byte)Clamp((int)(2 * m_intensity), 1, 8);
                particles.PartAcceleration = new Vector3(0.12f, 0.05f, -0.55f);
                return particles;
            }

            bool storm = weather == WeatherKind.Storm;
            float rainIntensity = storm ? m_intensity * 1.75f : m_intensity;

            particles.PartStartColor = storm
                ? new Color4(0.65f, 0.78f, 0.95f, 0.9f)
                : new Color4(0.72f, 0.88f, 1f, 0.82f);
            particles.PartEndColor = storm
                ? new Color4(0.5f, 0.65f, 0.82f, 0.08f)
                : new Color4(0.58f, 0.78f, 1f, 0.06f);
            particles.PartStartScaleX = storm ? 0.055f : 0.045f;
            particles.PartStartScaleY = storm ? 0.42f : 0.34f;
            particles.PartEndScaleX = storm ? 0.035f : 0.03f;
            particles.PartEndScaleY = storm ? 0.52f : 0.4f;
            particles.BurstSpeedMin = storm ? 0.2f : 0.08f;
            particles.BurstSpeedMax = storm ? 0.85f : 0.45f;
            particles.BurstRate = storm ? 0.055f : 0.075f;
            particles.PartMaxAge = storm ? 7.0f : 8.0f;
            particles.BurstPartCount = (byte)Clamp((int)(3 * rainIntensity), 1, 14);
            particles.PartAcceleration = storm
                ? new Vector3(0.85f, 0.25f, -2.8f)
                : new Vector3(0.28f, 0.1f, -1.8f);

            return particles;
        }

        private float JitteredCellPosition(int cell, float spacing, int regionSize)
        {
            double jitter = (m_random.NextDouble() - 0.5d) * spacing * 0.78d;
            float position = (float)(spacing * (cell + 0.5d) + jitter);
            return Clamp(position, 1f, regionSize - 1f);
        }

        private float JitterHeight()
        {
            return m_emitterHeight + (float)((m_random.NextDouble() - 0.5d) * m_emitterHeight * 0.35d);
        }

        private void ClearWeather(bool log)
        {
            List<SceneObjectGroup> emitters;
            lock (m_sync)
            {
                emitters = new List<SceneObjectGroup>(m_emitters);
                m_emitters.Clear();
                m_currentWeather = WeatherKind.Clear;
            }

            DeleteEmitters(emitters);

            if (log && m_scene != null)
                m_log.InfoFormat("[WEATHER]: Cleared weather in {0}", m_scene.RegionInfo.RegionName);
        }

        private void DeleteEmitters(List<SceneObjectGroup> emitters)
        {
            if (m_scene == null)
                return;

            foreach (SceneObjectGroup emitter in emitters)
            {
                if (emitter == null || emitter.IsDeleted)
                    continue;

                try
                {
                    m_scene.DeleteSceneObject(emitter, false, false);
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[WEATHER]: Failed to delete weather emitter {0}: {1}", emitter.UUID, e.Message);
                }
            }
        }

        private static bool IsWeatherCommand(string request)
        {
            string lower = request.ToLower(CultureInfo.InvariantCulture);
            return lower == "weather"
                || lower == "meteo"
                || lower.StartsWith("weather ")
                || lower.StartsWith("meteo ");
        }

        private bool TryResolveWeather(string request, out WeatherKind weather)
        {
            string lower = request.ToLower(CultureInfo.InvariantCulture);

            if (lower.Contains("clear") || lower.Contains("stop") || lower.Contains("sereno") || lower.Contains("asciutto"))
            {
                weather = WeatherKind.Clear;
                return true;
            }

            if (lower.Contains("storm") || lower.Contains("temporale") || lower.Contains("tempesta"))
            {
                weather = WeatherKind.Storm;
                return true;
            }

            if (lower.Contains("snow") || lower.Contains("neve") || lower.Contains("nevica"))
            {
                weather = WeatherKind.Snow;
                return true;
            }

            if (lower.Contains("rain") || lower.Contains("pioggia") || lower.Contains("piove"))
            {
                weather = WeatherKind.Rain;
                return true;
            }

            weather = WeatherKind.Clear;
            return false;
        }

        private static bool IsStatusCommand(string request)
        {
            string lower = request.ToLower(CultureInfo.InvariantCulture);
            return lower == "weather"
                || lower == "meteo"
                || lower.Contains("status")
                || lower.Contains("stato");
        }

        private void SendStatus(IClientAPI client)
        {
            int emitterCount;
            WeatherKind weather;
            lock (m_sync)
            {
                emitterCount = m_emitters.Count;
                weather = m_currentWeather;
            }

            SendReply(
                client,
                string.Format("Weather: {0}, emitters={1}.", WeatherName(weather), emitterCount));
        }

        private static string WeatherName(WeatherKind weather)
        {
            switch (weather)
            {
                case WeatherKind.Rain:
                    return "rain";
                case WeatherKind.Storm:
                    return "storm";
                case WeatherKind.Snow:
                    return "snow";
                default:
                    return "clear";
            }
        }

        private void SendReply(IClientAPI client, string message)
        {
            client.SendChatMessage(
                message,
                (byte)ChatTypeEnum.Owner,
                Vector3.Zero,
                "Weather",
                UUID.Zero,
                UUID.Zero,
                (byte)ChatSourceType.Object,
                (byte)ChatAudibleLevel.Fully);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}
