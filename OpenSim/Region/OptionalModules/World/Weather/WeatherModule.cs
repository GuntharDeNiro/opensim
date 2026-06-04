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
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.OptionalModules.World.Weather
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WeatherModule")]
    public class WeatherModule : INonSharedRegionModule
    {
        private enum WeatherKind
        {
            Clear,
            Sunny,
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
        private readonly object m_randomSync = new object();
        private readonly List<SceneObjectGroup> m_emitters = new List<SceneObjectGroup>();
        private readonly List<WeatherKind> m_autoCycleChoices = new List<WeatherKind>();
        private readonly Random m_random = new Random();

        private Scene m_scene;
        private bool m_enabled;
        private bool m_estateManagerOnly;
        private int m_commandChannel;
        private int m_emitterGrid;
        private float m_emitterHeight;
        private float m_intensity;
        private bool m_adjustClouds;
        private bool m_restoreCloudsOnClear;
        private IEnvironmentModule m_environmentModule;
        private RegionLightShareData m_savedEnvironment;
        private IWindModule m_windModule;
        private string m_savedWindPlugin;
        private Dictionary<string, float> m_savedWindParams;
        private WeatherKind m_currentWeather = WeatherKind.Clear;
        private int m_stormEffectGeneration;
        private bool m_adjustWind;
        private bool m_restoreWindOnClear;
        private float m_windDirectionDegrees;
        private float m_windDirectionVarianceDegrees;
        private float m_rainWindStrength;
        private float m_stormWindStrength;
        private float m_snowWindStrength;
        private bool m_avoidCoveredAreas;
        private float m_coverProbeHeight;
        private bool m_lightningEnabled;
        private int m_lightningMinDelayMS;
        private int m_lightningMaxDelayMS;
        private bool m_thunderEnabled;
        private UUID m_thunderSound = UUID.Zero;
        private float m_thunderVolume;
        private bool m_autoCycleEnabled;
        private float m_autoCycleHours;
        private int m_autoCycleStartupDelaySeconds;
        private bool m_autoCycleChangeOnStartup;
        private Timer m_autoCycleTimer;
        private Timer m_autoCycleWarningTimer;
        private int m_autoCycleBusy;
        private long m_nextAutoCycleTicks;
        private WeatherKind m_pendingAutoCycleWeather;
        private bool m_hasPendingAutoCycleWeather;
        private int m_autoCycleForecastWarningMinutes;
        private string m_autoCycleForecastWarningMessage;
        private bool m_sendWeatherIMOnEntry;
        private int m_weatherIMDelaySeconds;
        private string m_weatherIMMessage;

        public string Name { get { return "Weather Module"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["Weather"];
            if (config == null)
                m_log.Info("[WEATHER]: No [Weather] configuration found; using showroom defaults.");

            bool requestedEnabled = GetBoolean(config, "Enabled", true);
            bool allowDisabled = GetBoolean(config, "AllowDisabled", false);
            m_enabled = requestedEnabled || !allowDisabled;
            if (!requestedEnabled && m_enabled)
                m_log.Warn("[WEATHER]: Enabled=false ignored because AllowDisabled is not true; showroom weather remains enabled.");
            m_commandChannel = GetInt(config, "CommandChannel", 89);
            m_estateManagerOnly = GetBoolean(config, "EstateManagerOnly", true);
            m_emitterGrid = Math.Max(1, GetInt(config, "EmitterGrid", 8));
            m_emitterHeight = Math.Max(4f, GetFloat(config, "EmitterHeight", 18f));
            m_intensity = Clamp(GetFloat(config, "Intensity", 1f), 0.1f, 10f);
            m_adjustClouds = GetBoolean(config, "AdjustClouds", true);
            m_restoreCloudsOnClear = GetBoolean(config, "RestoreCloudsOnClear", true);
            m_adjustWind = GetBoolean(config, "AdjustWind", true);
            m_restoreWindOnClear = GetBoolean(config, "RestoreWindOnClear", true);
            m_windDirectionDegrees = GetFloat(config, "WindDirectionDegrees", 70f);
            m_windDirectionVarianceDegrees = Math.Max(0f, GetFloat(config, "WindDirectionVarianceDegrees", 18f));
            m_rainWindStrength = Math.Max(0f, GetFloat(config, "RainWindStrength", 0.45f));
            m_stormWindStrength = Math.Max(0f, GetFloat(config, "StormWindStrength", 1.35f));
            m_snowWindStrength = Math.Max(0f, GetFloat(config, "SnowWindStrength", 0.22f));
            m_avoidCoveredAreas = GetBoolean(config, "AvoidCoveredAreas", true);
            m_coverProbeHeight = Math.Max(8f, GetFloat(config, "CoverProbeHeight", 96f));
            m_lightningEnabled = GetBoolean(config, "LightningEnabled", true);
            m_lightningMinDelayMS = Math.Max(1000, GetInt(config, "LightningMinDelayMS", 7000));
            m_lightningMaxDelayMS = Math.Max(m_lightningMinDelayMS, GetInt(config, "LightningMaxDelayMS", 18000));
            m_thunderEnabled = GetBoolean(config, "ThunderEnabled", true);
            m_thunderVolume = Clamp(GetFloat(config, "ThunderVolume", 1f), 0f, 1f);
            m_autoCycleEnabled = GetBoolean(config, "AutoCycleEnabled", true);
            m_autoCycleHours = Math.Max(0.01f, GetFloat(config, "AutoCycleHours", 6f));
            m_autoCycleStartupDelaySeconds = Math.Max(1, GetInt(config, "AutoCycleStartupDelaySeconds", 30));
            m_autoCycleChangeOnStartup = GetBoolean(config, "AutoCycleChangeOnStartup", true);
            ParseAutoCycleChoices(GetString(config, "AutoCycleChoices", "storm,rain,snow,sunny,clear"));
            m_autoCycleForecastWarningMinutes = Math.Max(0, GetInt(config, "AutoCycleForecastWarningMinutes", 15));
            m_autoCycleForecastWarningMessage = GetString(
                config,
                "AutoCycleForecastWarningMessage",
                "Weather forecast update: next conditions for {RegionName} are expected to shift to {NextWeather} in {TimeUntilNextForecast}.").Trim();
            m_sendWeatherIMOnEntry = GetBoolean(config, "SendWeatherIMOnEntry", true);
            m_weatherIMDelaySeconds = Math.Max(0, GetInt(config, "WeatherIMDelaySeconds", 8));
            m_weatherIMMessage = GetString(
                config,
                "WeatherIMMessage",
                "Weather forecast for {RegionName}: current conditions are {Weather}. Next forecast: {NextForecast}. Stay tuned for further regional updates.").Trim();

            string thunderSound = GetString(config, "ThunderSound", string.Empty);
            if (!string.IsNullOrEmpty(thunderSound) && !UUID.TryParse(thunderSound, out m_thunderSound))
                m_log.WarnFormat("[WEATHER]: ThunderSound '{0}' is not a valid UUID; thunder audio disabled.", thunderSound);
        }

        private static bool GetBoolean(IConfig config, string key, bool defaultValue)
        {
            return config == null ? defaultValue : config.GetBoolean(key, defaultValue);
        }

        private static int GetInt(IConfig config, string key, int defaultValue)
        {
            return config == null ? defaultValue : config.GetInt(key, defaultValue);
        }

        private static float GetFloat(IConfig config, string key, float defaultValue)
        {
            return config == null ? defaultValue : config.GetFloat(key, defaultValue);
        }

        private static string GetString(IConfig config, string key, string defaultValue)
        {
            return config == null ? defaultValue : config.GetString(key, defaultValue);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            m_scene.EventManager.OnChatFromClient += OnChatFromClient;
            m_scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
            m_log.InfoFormat(
                "[WEATHER]: Enabled in region {0} on channel {1}",
                scene.RegionInfo.RegionName,
                m_commandChannel);
        }

        public void RemoveRegion(Scene scene)
        {
            StopAutoCycle();

            if (m_scene != null)
            {
                m_scene.EventManager.OnChatFromClient -= OnChatFromClient;
                m_scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
            }

            ClearWeather(false, true);
            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            m_environmentModule = scene.RequestModuleInterface<IEnvironmentModule>();
            if (m_adjustClouds && m_environmentModule == null)
                m_log.WarnFormat("[WEATHER]: Cloud changes disabled in {0}; EnvironmentModule is not available.", scene.RegionInfo.RegionName);

            m_windModule = scene.RequestModuleInterface<IWindModule>();
            if (m_adjustWind && m_windModule == null)
                m_log.WarnFormat("[WEATHER]: Region wind changes disabled in {0}; WindModule is not available.", scene.RegionInfo.RegionName);

            StartAutoCycle();
        }

        public void Close()
        {
            StopAutoCycle();
            ClearWeather(false, true);
        }

        private void OnMakeRootAgent(ScenePresence sp)
        {
            if (!m_sendWeatherIMOnEntry || sp == null || sp.IsDeleted || sp.IsNPC || sp.IsChildAgent)
                return;

            Util.FireAndForget(
                o =>
                {
                    if (m_weatherIMDelaySeconds > 0)
                        Thread.Sleep(m_weatherIMDelaySeconds * 1000);

                    SendWeatherIM(sp.UUID);
                },
                null,
                "WeatherModule.EntryIM",
                false);
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
                SendReply(client, "Weather: use rain, storm, snow, sunny, clear, or status.");
                return;
            }

            if (weather == WeatherKind.Clear)
            {
                ClearWeather(true, true);
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
            ClearWeather(false, false);

            if (m_scene == null)
                return false;

            List<SceneObjectGroup> created = new List<SceneObjectGroup>();

            if (weather != WeatherKind.Sunny)
            {
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
                        if (IsCoveredFromSky(posX, posY, ground, position.Z))
                            continue;

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
            }

            lock (m_sync)
            {
                m_emitters.AddRange(created);
                m_currentWeather = weather;
            }

            ApplyClouds(weather);
            ApplyWind(weather);
            if (weather == WeatherKind.Storm)
                StartStormEffects(ownerId);

            m_log.InfoFormat(
                "[WEATHER]: Started {0} in {1} with {2} emitters",
                WeatherName(weather),
                m_scene.RegionInfo.RegionName,
                created.Count);

            return true;
        }

        private void StartAutoCycle()
        {
            if (!m_autoCycleEnabled || m_scene == null)
                return;

            StopAutoCycle();

            int dueTime = m_autoCycleChangeOnStartup
                ? m_autoCycleStartupDelaySeconds * 1000
                : AutoCycleIntervalMS();

            m_autoCycleTimer = new Timer(AutoCycleTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
            m_autoCycleWarningTimer = new Timer(AutoCycleForecastWarningElapsed, null, Timeout.Infinite, Timeout.Infinite);
            ScheduleAutoCycle(dueTime);

            m_log.InfoFormat(
                "[WEATHER]: Auto cycle enabled in {0}; choices={1}, interval={2:0.##} hours",
                m_scene.RegionInfo.RegionName,
                AutoCycleChoiceNames(),
                m_autoCycleHours);
        }

        private void StopAutoCycle()
        {
            Timer timer = m_autoCycleTimer;
            m_autoCycleTimer = null;
            Timer warningTimer = m_autoCycleWarningTimer;
            m_autoCycleWarningTimer = null;

            if (timer != null)
                timer.Dispose();
            if (warningTimer != null)
                warningTimer.Dispose();

            Interlocked.Exchange(ref m_autoCycleBusy, 0);
            m_nextAutoCycleTicks = 0;
            m_hasPendingAutoCycleWeather = false;
        }

        private void AutoCycleTimerElapsed(object state)
        {
            if (Interlocked.Exchange(ref m_autoCycleBusy, 1) != 0)
                return;

            try
            {
                if (m_scene == null)
                    return;

                WeatherKind weather;
                lock (m_sync)
                {
                    weather = m_hasPendingAutoCycleWeather ? m_pendingAutoCycleWeather : PickAutoCycleWeather();
                    m_hasPendingAutoCycleWeather = false;
                }

                if (weather == WeatherKind.Clear)
                {
                    ClearWeather(true, true);
                    m_log.InfoFormat("[WEATHER]: Auto cycle selected clear in {0}", m_scene.RegionInfo.RegionName);
                }
                else if (ApplyWeather(weather, UUID.Zero))
                {
                    m_log.InfoFormat(
                        "[WEATHER]: Auto cycle selected {0} in {1}",
                        WeatherName(weather),
                        m_scene.RegionInfo.RegionName);
                }
                else
                {
                    m_log.WarnFormat(
                        "[WEATHER]: Auto cycle could not start {0} in {1}",
                        WeatherName(weather),
                        m_scene.RegionInfo.RegionName);
                }
            }
            catch (Exception e)
            {
                string regionName = m_scene == null ? "unknown region" : m_scene.RegionInfo.RegionName;
                m_log.WarnFormat("[WEATHER]: Auto cycle failed in {0}: {1}", regionName, e);
            }
            finally
            {
                Interlocked.Exchange(ref m_autoCycleBusy, 0);
                ScheduleNextAutoCycle();
            }
        }

        private void ScheduleNextAutoCycle()
        {
            ScheduleAutoCycle(AutoCycleIntervalMS());
        }

        private void ScheduleAutoCycle(int dueTimeMS)
        {
            Timer timer = m_autoCycleTimer;
            if (timer == null || m_scene == null)
                return;

            dueTimeMS = Math.Max(1000, dueTimeMS);
            m_nextAutoCycleTicks = DateTime.Now.Ticks + dueTimeMS * 10000L;
            timer.Change(dueTimeMS, Timeout.Infinite);

            ScheduleAutoCycleWarning(dueTimeMS);
        }

        private void ScheduleAutoCycleWarning(int dueTimeMS)
        {
            Timer warningTimer = m_autoCycleWarningTimer;
            if (warningTimer == null || m_autoCycleForecastWarningMinutes <= 0 || string.IsNullOrEmpty(m_autoCycleForecastWarningMessage))
                return;

            int warningLeadMS = m_autoCycleForecastWarningMinutes * 60 * 1000;
            int warningDueMS = dueTimeMS - warningLeadMS;
            if (warningDueMS <= 0)
            {
                warningTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            warningTimer.Change(warningDueMS, Timeout.Infinite);
        }

        private void AutoCycleForecastWarningElapsed(object state)
        {
            Scene scene = m_scene;
            if (scene == null || string.IsNullOrEmpty(m_autoCycleForecastWarningMessage))
                return;

            WeatherKind nextWeather;
            lock (m_sync)
            {
                if (!m_hasPendingAutoCycleWeather)
                {
                    m_pendingAutoCycleWeather = PickAutoCycleWeather();
                    m_hasPendingAutoCycleWeather = true;
                }

                nextWeather = m_pendingAutoCycleWeather;
            }

            string message = FormatForecastWarningMessage(scene, nextWeather);
            scene.ForEachRootScenePresence(sp =>
            {
                if (sp != null && !sp.IsDeleted && sp.ControllingClient != null)
                    SendRegionWeatherMessage(sp, message);
            });
        }

        private int AutoCycleIntervalMS()
        {
            return Math.Max(1000, (int)Math.Min(int.MaxValue, m_autoCycleHours * 60f * 60f * 1000f));
        }

        private WeatherKind PickAutoCycleWeather()
        {
            WeatherKind current;
            lock (m_sync)
                current = m_currentWeather;

            lock (m_randomSync)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    WeatherKind weather = m_autoCycleChoices[m_random.Next(m_autoCycleChoices.Count)];
                    if (m_autoCycleChoices.Count == 1 || weather != current)
                        return weather;
                }

                return m_autoCycleChoices[m_random.Next(m_autoCycleChoices.Count)];
            }
        }

        private void ParseAutoCycleChoices(string choices)
        {
            m_autoCycleChoices.Clear();

            string[] tokens = (choices ?? string.Empty).Split(',');
            foreach (string rawToken in tokens)
            {
                WeatherKind weather;
                if (TryResolveWeather(rawToken.Trim(), out weather) && !m_autoCycleChoices.Contains(weather))
                    m_autoCycleChoices.Add(weather);
            }

            if (m_autoCycleChoices.Count == 0)
            {
                m_autoCycleChoices.Add(WeatherKind.Storm);
                m_autoCycleChoices.Add(WeatherKind.Rain);
                m_autoCycleChoices.Add(WeatherKind.Snow);
                m_autoCycleChoices.Add(WeatherKind.Sunny);
            }
        }

        private string AutoCycleChoiceNames()
        {
            List<string> names = new List<string>(m_autoCycleChoices.Count);
            foreach (WeatherKind weather in m_autoCycleChoices)
                names.Add(WeatherName(weather));

            return string.Join(",", names.ToArray());
        }

        private bool IsCoveredFromSky(float x, float y, float ground, float emitterZ)
        {
            if (!m_avoidCoveredAreas || m_scene == null || !m_scene.SupportsRayCastFiltered())
                return false;

            float startZ = Math.Max(emitterZ + 2f, ground + m_coverProbeHeight);
            float length = Math.Max(1f, startZ - ground - 0.2f);
            Vector3 start = new Vector3(x, y, startZ);

            try
            {
                RayFilterFlags filter = RayFilterFlags.AllPrims | RayFilterFlags.ClosestHit;
                List<ContactResult> hits = (List<ContactResult>)m_scene.RayCastFiltered(start, -Vector3.UnitZ, length, 1, filter);
                return hits != null && hits.Count > 0;
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[WEATHER]: Cover probe failed in {0}: {1}", m_scene.RegionInfo.RegionName, e.Message);
                return false;
            }
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
            root.AddNewParticleSystem(CreateParticleSystem(weather, radius, RandomRange(0.72f, 1.32f)), false);

            SceneObjectGroup group = new SceneObjectGroup(root);
            group.SetGroup(UUID.Zero, null);
            return group;
        }

        private Primitive.ParticleSystem CreateParticleSystem(WeatherKind weather, float radius, float emitterVariance)
        {
            float densityVariance = RandomRange(0.65f, 1.35f);
            float driftVariance = RandomRange(0.75f, 1.25f);

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
                particles.BurstRate = RandomRange(0.045f, 0.085f) * emitterVariance;
                particles.PartMaxAge = 12.0f;
                particles.BurstPartCount = (byte)Clamp((int)Math.Ceiling(1.2f * m_intensity * densityVariance), 1, 5);
                Vector2 snowWind = WeatherWindVector(weather, driftVariance);
                particles.PartAcceleration = new Vector3(snowWind.X, snowWind.Y, -0.55f);
                return particles;
            }

            bool storm = weather == WeatherKind.Storm;
            float rainIntensity = storm ? m_intensity * 1.75f : m_intensity;

            particles.PartStartColor = storm
                ? new Color4(0.66f, 0.69f, 0.74f, 0.94f)
                : new Color4(0.7f, 0.73f, 0.76f, 0.86f);
            particles.PartEndColor = storm
                ? new Color4(0.5f, 0.54f, 0.6f, 0.1f)
                : new Color4(0.56f, 0.6f, 0.65f, 0.08f);
            particles.PartStartScaleX = storm ? 0.038f : 0.032f;
            particles.PartStartScaleY = storm ? 0.38f : 0.3f;
            particles.PartEndScaleX = storm ? 0.024f : 0.022f;
            particles.PartEndScaleY = storm ? 0.48f : 0.36f;
            particles.BurstSpeedMin = storm ? 0.65f : 0.38f;
            particles.BurstSpeedMax = storm ? 2.2f : 1.45f;
            particles.BurstRate = (storm ? RandomRange(0.024f, 0.045f) : RandomRange(0.032f, 0.06f)) * emitterVariance;
            particles.PartMaxAge = storm ? 1.9f : 2.35f;
            particles.BurstPartCount = (byte)Clamp((int)Math.Ceiling(1.4f * rainIntensity * densityVariance), 1, storm ? 28 : 20);
            Vector2 rainWind = WeatherWindVector(weather, driftVariance);
            particles.PartAcceleration = new Vector3(rainWind.X, rainWind.Y, storm ? -18f : -12f);

            return particles;
        }

        private Vector2 WeatherWindVector(WeatherKind weather, float variance)
        {
            float strength = WeatherWindStrength(weather);
            if (strength <= 0f)
                return Vector2.Zero;

            float angle = m_windDirectionDegrees + RandomRange(-m_windDirectionVarianceDegrees, m_windDirectionVarianceDegrees);
            double radians = angle * Math.PI / 180d;
            return new Vector2(
                (float)Math.Cos(radians) * strength * variance,
                (float)Math.Sin(radians) * strength * variance);
        }

        private float WeatherWindStrength(WeatherKind weather)
        {
            switch (weather)
            {
                case WeatherKind.Storm:
                    return m_stormWindStrength;
                case WeatherKind.Snow:
                    return m_snowWindStrength;
                case WeatherKind.Rain:
                    return m_rainWindStrength;
                default:
                    return 0f;
            }
        }

        private float JitteredCellPosition(int cell, float spacing, int regionSize)
        {
            double jitter = (RandomUnit() - 0.5d) * spacing * 0.78d;
            float position = (float)(spacing * (cell + 0.5d) + jitter);
            return Clamp(position, 1f, regionSize - 1f);
        }

        private float JitterHeight()
        {
            return m_emitterHeight + (float)((RandomUnit() - 0.5d) * m_emitterHeight * 0.35d);
        }

        private float RandomRange(float min, float max)
        {
            return min + (float)RandomUnit() * (max - min);
        }

        private int RandomRange(int min, int max)
        {
            lock (m_randomSync)
                return m_random.Next(min, max);
        }

        private double RandomUnit()
        {
            lock (m_randomSync)
                return m_random.NextDouble();
        }

        private void ClearWeather(bool log, bool restoreClouds)
        {
            StopStormEffects();

            List<SceneObjectGroup> emitters;
            lock (m_sync)
            {
                emitters = new List<SceneObjectGroup>(m_emitters);
                m_emitters.Clear();
                m_currentWeather = WeatherKind.Clear;
            }

            DeleteEmitters(emitters);

            if (restoreClouds)
                RestoreClouds();
            if (restoreClouds)
                RestoreWind();

            if (log && m_scene != null)
                m_log.InfoFormat("[WEATHER]: Cleared weather in {0}", m_scene.RegionInfo.RegionName);
        }

        private void StartStormEffects(UUID ownerId)
        {
            if (!m_lightningEnabled && (!m_thunderEnabled || m_thunderSound.IsZero()))
                return;

            int generation = Interlocked.Increment(ref m_stormEffectGeneration);
            Util.FireAndForget(
                o => StormEffectLoop(generation, ownerId),
                null,
                "WeatherModule.StormEffects",
                false);
        }

        private void StopStormEffects()
        {
            Interlocked.Increment(ref m_stormEffectGeneration);
        }

        private void StormEffectLoop(int generation, UUID ownerId)
        {
            while (generation == m_stormEffectGeneration && IsCurrentWeather(WeatherKind.Storm))
            {
                Thread.Sleep(RandomRange(m_lightningMinDelayMS, m_lightningMaxDelayMS + 1));

                if (m_scene == null || generation != m_stormEffectGeneration || !IsCurrentWeather(WeatherKind.Storm))
                    return;

                TriggerLightning(ownerId, generation);
            }
        }

        private bool IsCurrentWeather(WeatherKind weather)
        {
            lock (m_sync)
                return m_currentWeather == weather;
        }

        private void TriggerLightning(UUID ownerId, int generation)
        {
            if (m_scene == null)
                return;

            Vector3 position = GetLightningPosition();

            if (m_lightningEnabled)
                CreateLightningFlash(ownerId, position);

            if (m_thunderEnabled && !m_thunderSound.IsZero())
            {
                Util.FireAndForget(
                    o =>
                    {
                        Thread.Sleep(RandomRange(450, 1900));
                        if (generation == m_stormEffectGeneration && IsCurrentWeather(WeatherKind.Storm))
                            SendThunder(position);
                    },
                    null,
                    "WeatherModule.Thunder",
                    false);
            }
        }

        private Vector3 GetLightningPosition()
        {
            int sizeX = Math.Max(1, (int)m_scene.RegionInfo.RegionSizeX);
            int sizeY = Math.Max(1, (int)m_scene.RegionInfo.RegionSizeY);
            List<ScenePresence> avatars = new List<ScenePresence>();

            m_scene.ForEachRootScenePresence(sp =>
            {
                if (sp != null && !sp.IsDeleted)
                    avatars.Add(sp);
            });

            float x;
            float y;
            if (avatars.Count > 0)
            {
                ScenePresence avatar = avatars[RandomRange(0, avatars.Count)];
                x = Clamp(avatar.AbsolutePosition.X + RandomRange(-52f, 52f), 6f, sizeX - 6f);
                y = Clamp(avatar.AbsolutePosition.Y + RandomRange(-52f, 52f), 6f, sizeY - 6f);
            }
            else
            {
                x = RandomRange(6f, sizeX - 6f);
                y = RandomRange(6f, sizeY - 6f);
            }

            float ground = m_scene.GetGroundHeight(x, y);
            return new Vector3(x, y, ground + m_emitterHeight + RandomRange(5f, 13f));
        }

        private void CreateLightningFlash(UUID ownerId, Vector3 position)
        {
            PrimitiveBaseShape shape = PrimitiveBaseShape.CreateCylinder();
            shape.Scale = new Vector3(0.55f, 0.55f, RandomRange(26f, 46f));

            Primitive.TextureEntry textures = shape.Textures;
            textures.DefaultTexture.RGBA = new Color4(0.96f, 0.98f, 1f, 1f);
            textures.DefaultTexture.Fullbright = true;
            textures.DefaultTexture.Glow = 1f;
            shape.Textures = textures;

            SceneObjectPart root = new SceneObjectPart(ownerId, shape, position, Quaternion.Identity, Vector3.Zero);
            root.Name = "weather lightning flash";
            root.Scale = shape.Scale;
            root.AddFlag(PrimFlags.Phantom);

            SceneObjectGroup flash = new SceneObjectGroup(root);
            flash.SetGroup(UUID.Zero, null);

            if (!m_scene.AddNewSceneObject(flash, false))
                return;

            flash.RootPart.SendFullUpdateToAllClients();
            flash.ScheduleGroupForUpdate(PrimUpdateFlags.FullUpdate);

            Util.FireAndForget(
                o =>
                {
                    Thread.Sleep(RandomRange(420, 820));
                    DeleteLightningFlash(flash);
                },
                null,
                "WeatherModule.LightningFlash",
                false);
        }

        private void DeleteLightningFlash(SceneObjectGroup flash)
        {
            if (m_scene == null || flash == null || flash.IsDeleted)
                return;

            try
            {
                m_scene.DeleteSceneObject(flash, false, false);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[WEATHER]: Failed to delete lightning flash {0}: {1}", flash.UUID, e.Message);
            }
        }

        private void SendThunder(Vector3 position)
        {
            m_scene.ForEachRootScenePresence(sp =>
            {
                if (sp == null || sp.IsDeleted || sp.ControllingClient == null)
                    return;

                Vector3 audiblePosition = sp.AbsolutePosition;
                sp.ControllingClient.SendTriggeredSound(
                    m_thunderSound,
                    UUID.Zero,
                    UUID.Zero,
                    UUID.Zero,
                    m_scene.RegionInfo.RegionHandle,
                    audiblePosition,
                    m_thunderVolume);
            });
        }

        private void ApplyClouds(WeatherKind weather)
        {
            if (!m_adjustClouds || m_environmentModule == null)
                return;

            RegionLightShareData current = m_savedEnvironment ?? m_environmentModule.ToLightShare();
            if (current == null)
                return;

            if (m_savedEnvironment == null)
                m_savedEnvironment = CloneLightShare(current);

            RegionLightShareData clouds = CloneLightShare(current);

            if (weather == WeatherKind.Sunny)
            {
                clouds.cloudCoverage = 0.05f;
                clouds.cloudScale = 0.28f;
                clouds.cloudColor = new Vector4(1.0f, 0.98f, 0.9f, 1f);
                clouds.cloudXYDensity = new Vector3(0.38f, 0.18f, 0.28f);
                clouds.cloudDetailXYDensity = new Vector3(0.34f, 0.16f, 0.04f);
                clouds.cloudScrollX = 0.06f;
                clouds.cloudScrollY = 0.012f;
                clouds.horizon = new Vector4(0.58f, 0.72f, 0.95f, 1f);
                clouds.blueDensity = new Vector4(0.16f, 0.32f, 0.68f, 1f);
                clouds.ambient = new Vector4(0.48f, 0.48f, 0.42f, 1f);
                clouds.sunMoonColor = new Vector4(1.0f, 0.88f, 0.52f, 1f);
                clouds.hazeHorizon = Math.Min(clouds.hazeHorizon, 0.1f);
                clouds.hazeDensity = Math.Min(clouds.hazeDensity, 0.18f);
                clouds.densityMultiplier = Math.Min(clouds.densityMultiplier, 0.08f);
                clouds.distanceMultiplier = Math.Max(clouds.distanceMultiplier, 1.15f);
                clouds.sunGlowFocus = Math.Max(clouds.sunGlowFocus, 0.22f);
                clouds.sunGlowSize = Math.Max(clouds.sunGlowSize, 2.4f);
                clouds.sceneGamma = Math.Max(clouds.sceneGamma, 1.16f);
                clouds.starBrightness = 0f;
            }
            else if (weather == WeatherKind.Storm)
            {
                clouds.cloudCoverage = 0.94f;
                clouds.cloudScale = 0.72f;
                clouds.cloudColor = new Vector4(0.06f, 0.07f, 0.085f, 1f);
                clouds.cloudXYDensity = new Vector3(1.35f, 0.64f, 1.0f);
                clouds.cloudDetailXYDensity = new Vector3(1.55f, 0.72f, 0.28f);
                clouds.cloudScrollX = 0.48f;
                clouds.cloudScrollY = 0.1f;
                clouds.horizon = new Vector4(0.08f, 0.09f, 0.11f, 1f);
                clouds.blueDensity = new Vector4(0.04f, 0.055f, 0.08f, 1f);
                clouds.ambient = new Vector4(0.08f, 0.085f, 0.095f, 1f);
                clouds.sunMoonColor = new Vector4(0.12f, 0.13f, 0.15f, 1f);
                clouds.hazeHorizon = Math.Max(clouds.hazeHorizon, 0.45f);
                clouds.hazeDensity = Math.Max(clouds.hazeDensity, 0.92f);
                clouds.densityMultiplier = Math.Max(clouds.densityMultiplier, 0.32f);
                clouds.sceneGamma = clouds.sceneGamma <= 0f ? 0.72f : Math.Min(clouds.sceneGamma, 0.72f);
            }
            else if (weather == WeatherKind.Snow)
            {
                clouds.cloudCoverage = 0.86f;
                clouds.cloudScale = 0.7f;
                clouds.cloudColor = new Vector4(0.38f, 0.4f, 0.44f, 1f);
                clouds.cloudXYDensity = new Vector3(1.18f, 0.6f, 0.96f);
                clouds.cloudDetailXYDensity = new Vector3(1.28f, 0.62f, 0.22f);
                clouds.cloudScrollX = 0.14f;
                clouds.cloudScrollY = 0.035f;
                clouds.horizon = new Vector4(0.26f, 0.28f, 0.32f, 1f);
                clouds.blueDensity = new Vector4(0.13f, 0.15f, 0.2f, 1f);
                clouds.ambient = new Vector4(0.22f, 0.23f, 0.25f, 1f);
                clouds.sunMoonColor = new Vector4(0.3f, 0.31f, 0.34f, 1f);
                clouds.hazeHorizon = Math.Max(clouds.hazeHorizon, 0.36f);
                clouds.hazeDensity = Math.Max(clouds.hazeDensity, 0.72f);
                clouds.densityMultiplier = Math.Max(clouds.densityMultiplier, 0.24f);
                clouds.sceneGamma = clouds.sceneGamma <= 0f ? 0.86f : Math.Min(clouds.sceneGamma, 0.86f);
            }
            else
            {
                clouds.cloudCoverage = 0.82f;
                clouds.cloudScale = 0.64f;
                clouds.cloudColor = new Vector4(0.16f, 0.18f, 0.22f, 1f);
                clouds.cloudXYDensity = new Vector3(1.18f, 0.6f, 0.96f);
                clouds.cloudDetailXYDensity = new Vector3(1.32f, 0.64f, 0.24f);
                clouds.cloudScrollX = 0.28f;
                clouds.cloudScrollY = 0.05f;
                clouds.horizon = new Vector4(0.15f, 0.17f, 0.2f, 1f);
                clouds.blueDensity = new Vector4(0.07f, 0.09f, 0.13f, 1f);
                clouds.ambient = new Vector4(0.14f, 0.15f, 0.17f, 1f);
                clouds.sunMoonColor = new Vector4(0.2f, 0.21f, 0.24f, 1f);
                clouds.hazeHorizon = Math.Max(clouds.hazeHorizon, 0.4f);
                clouds.hazeDensity = Math.Max(clouds.hazeDensity, 0.82f);
                clouds.densityMultiplier = Math.Max(clouds.densityMultiplier, 0.28f);
                clouds.sceneGamma = clouds.sceneGamma <= 0f ? 0.78f : Math.Min(clouds.sceneGamma, 0.78f);
            }

            clouds.drawClassicClouds = true;
            clouds.cloudScrollXLock = false;
            clouds.cloudScrollYLock = false;
            m_environmentModule.FromLightShare(clouds);
        }

        private void ApplyWind(WeatherKind weather)
        {
            if (!m_adjustWind || m_windModule == null)
                return;

            string plugin = m_windModule.WindActiveModelPluginName;
            if (string.IsNullOrEmpty(plugin))
                return;

            try
            {
                SaveWindParams(plugin);

                float strength = WeatherWindStrength(weather);
                if (plugin == "SimpleRandomWind")
                {
                    m_windModule.WindParamSet(plugin, "strength", strength * 4f);
                }
                else if (plugin == "ConfigurableWind")
                {
                    m_windModule.WindParamSet(plugin, "avgStrength", strength * 4f);
                    m_windModule.WindParamSet(plugin, "avgDirection", m_windDirectionDegrees);
                    m_windModule.WindParamSet(plugin, "varStrength", Math.Max(0.5f, strength * 1.6f));
                    m_windModule.WindParamSet(plugin, "varDirection", m_windDirectionVarianceDegrees);
                    m_windModule.WindParamSet(plugin, "rateChange", weather == WeatherKind.Storm ? 1.8f : 0.8f);
                }
            }
            catch (Exception e)
            {
                string regionName = m_scene == null ? "unknown region" : m_scene.RegionInfo.RegionName;
                m_log.DebugFormat("[WEATHER]: Could not apply weather wind in {0}: {1}", regionName, e.Message);
            }
        }

        private void SaveWindParams(string plugin)
        {
            if (m_savedWindParams != null)
                return;

            Dictionary<string, float> saved = new Dictionary<string, float>();

            if (plugin == "SimpleRandomWind")
            {
                saved["strength"] = m_windModule.WindParamGet(plugin, "strength");
            }
            else if (plugin == "ConfigurableWind")
            {
                saved["avgStrength"] = m_windModule.WindParamGet(plugin, "avgStrength");
                saved["avgDirection"] = m_windModule.WindParamGet(plugin, "avgDirection");
                saved["varStrength"] = m_windModule.WindParamGet(plugin, "varStrength");
                saved["varDirection"] = m_windModule.WindParamGet(plugin, "varDirection");
                saved["rateChange"] = m_windModule.WindParamGet(plugin, "rateChange");
            }

            if (saved.Count == 0)
                return;

            m_savedWindPlugin = plugin;
            m_savedWindParams = saved;
        }

        private void RestoreWind()
        {
            if (!m_restoreWindOnClear || m_windModule == null || m_savedWindParams == null || string.IsNullOrEmpty(m_savedWindPlugin))
                return;

            try
            {
                foreach (KeyValuePair<string, float> windParam in m_savedWindParams)
                    m_windModule.WindParamSet(m_savedWindPlugin, windParam.Key, windParam.Value);
            }
            catch (Exception e)
            {
                string regionName = m_scene == null ? "unknown region" : m_scene.RegionInfo.RegionName;
                m_log.DebugFormat("[WEATHER]: Could not restore weather wind in {0}: {1}", regionName, e.Message);
            }

            m_savedWindPlugin = null;
            m_savedWindParams = null;
        }

        private void RestoreClouds()
        {
            if (!m_restoreCloudsOnClear || m_environmentModule == null || m_savedEnvironment == null)
                return;

            m_environmentModule.FromLightShare(m_savedEnvironment);
            m_savedEnvironment = null;
        }

        private static RegionLightShareData CloneLightShare(RegionLightShareData source)
        {
            return new RegionLightShareData
            {
                waterColor = source.waterColor,
                waterFogDensityExponent = source.waterFogDensityExponent,
                underwaterFogModifier = source.underwaterFogModifier,
                reflectionWaveletScale = source.reflectionWaveletScale,
                fresnelScale = source.fresnelScale,
                fresnelOffset = source.fresnelOffset,
                refractScaleAbove = source.refractScaleAbove,
                refractScaleBelow = source.refractScaleBelow,
                blurMultiplier = source.blurMultiplier,
                bigWaveDirection = source.bigWaveDirection,
                littleWaveDirection = source.littleWaveDirection,
                normalMapTexture = source.normalMapTexture,
                horizon = source.horizon,
                hazeHorizon = source.hazeHorizon,
                blueDensity = source.blueDensity,
                hazeDensity = source.hazeDensity,
                densityMultiplier = source.densityMultiplier,
                distanceMultiplier = source.distanceMultiplier,
                maxAltitude = source.maxAltitude,
                sunMoonColor = source.sunMoonColor,
                sunMoonPosition = source.sunMoonPosition,
                ambient = source.ambient,
                eastAngle = source.eastAngle,
                sunGlowFocus = source.sunGlowFocus,
                sunGlowSize = source.sunGlowSize,
                sceneGamma = source.sceneGamma,
                starBrightness = source.starBrightness,
                cloudColor = source.cloudColor,
                cloudXYDensity = source.cloudXYDensity,
                cloudCoverage = source.cloudCoverage,
                cloudScale = source.cloudScale,
                cloudDetailXYDensity = source.cloudDetailXYDensity,
                cloudScrollX = source.cloudScrollX,
                cloudScrollXLock = source.cloudScrollXLock,
                cloudScrollY = source.cloudScrollY,
                cloudScrollYLock = source.cloudScrollYLock,
                drawClassicClouds = source.drawClassicClouds
            };
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

            if (lower.Contains("clear") || lower.Contains("stop") || lower.Contains("asciutto"))
            {
                weather = WeatherKind.Clear;
                return true;
            }

            if (lower.Contains("sunny") || lower.Contains("sun") || lower.Contains("sereno") || lower.Contains("sole"))
            {
                weather = WeatherKind.Sunny;
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
                string.Format(
                    "Weather: {0}, emitters={1}, auto={2}.",
                    WeatherName(weather),
                    emitterCount,
                    m_autoCycleEnabled ? string.Format("{0:0.##}h", m_autoCycleHours) : "off"));
        }

        private void SendWeatherIM(UUID agentID)
        {
            Scene scene = m_scene;
            if (scene == null || string.IsNullOrEmpty(m_weatherIMMessage))
                return;

            ScenePresence sp = scene.GetScenePresence(agentID);
            if (sp == null || sp.IsDeleted || sp.IsNPC || sp.IsChildAgent || sp.ControllingClient == null)
                return;

            GridInstantMessage msg = new GridInstantMessage
            {
                imSessionID = UUID.Random().Guid,
                fromAgentID = UUID.Zero.Guid,
                toAgentID = agentID.Guid,
                timestamp = (uint)Util.UnixTimeSinceEpoch(),
                fromAgentName = "Weather",
                message = FormatWeatherIMMessage(scene),
                dialog = (byte)InstantMessageDialog.MessageFromAgent,
                fromGroup = false,
                offline = (byte)0,
                ParentEstateID = 0,
                Position = sp.AbsolutePosition,
                RegionID = scene.RegionInfo.RegionID.Guid,
                binaryBucket = Array.Empty<byte>()
            };

            sp.ControllingClient.SendInstantMessage(msg);
        }

        private string FormatForecastWarningMessage(Scene scene, WeatherKind nextWeather)
        {
            return m_autoCycleForecastWarningMessage
                .Replace("{RegionName}", scene.RegionInfo.RegionName)
                .Replace("{NextWeather}", WeatherName(nextWeather))
                .Replace("{TimeUntilNextForecast}", FormatTimeUntilNextForecast())
                .Replace("{NextForecast}", FormatNextForecast());
        }

        private void SendRegionWeatherMessage(ScenePresence sp, string message)
        {
            sp.ControllingClient.SendChatMessage(
                message,
                (byte)ChatTypeEnum.Region,
                Vector3.Zero,
                "Weather",
                UUID.Zero,
                UUID.Zero,
                (byte)ChatSourceType.Object,
                (byte)ChatAudibleLevel.Fully);
        }

        private string FormatWeatherIMMessage(Scene scene)
        {
            int emitterCount;
            WeatherKind weather;
            lock (m_sync)
            {
                emitterCount = m_emitters.Count;
                weather = m_currentWeather;
            }

            return m_weatherIMMessage
                .Replace("{RegionName}", scene.RegionInfo.RegionName)
                .Replace("{Weather}", WeatherName(weather))
                .Replace("{EmitterCount}", emitterCount.ToString(CultureInfo.InvariantCulture))
                .Replace("{AutoCycle}", m_autoCycleEnabled ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}h", m_autoCycleHours) : "off")
                .Replace("{NextForecast}", FormatNextForecast());
        }

        private string FormatNextForecast()
        {
            if (!m_autoCycleEnabled)
                return "manual updates only";

            string timeUntil = FormatTimeUntilNextForecast();
            if (string.IsNullOrEmpty(timeUntil))
                return "scheduled shortly";

            return "in " + timeUntil;
        }

        private string FormatTimeUntilNextForecast()
        {
            long nextTicks = m_nextAutoCycleTicks;
            if (nextTicks <= 0)
                return string.Empty;

            TimeSpan remaining = new TimeSpan(Math.Max(0, nextTicks - DateTime.Now.Ticks));
            if (remaining.TotalSeconds < 60)
                return "less than 1 minute";

            int hours = (int)Math.Floor(remaining.TotalHours);
            int minutes = remaining.Minutes;
            if (hours > 0 && minutes > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0} hour{1} {2} minute{3}", hours, hours == 1 ? string.Empty : "s", minutes, minutes == 1 ? string.Empty : "s");
            if (hours > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0} hour{1}", hours, hours == 1 ? string.Empty : "s");

            return string.Format(CultureInfo.InvariantCulture, "{0} minute{1}", Math.Max(1, minutes), minutes == 1 ? string.Empty : "s");
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
                case WeatherKind.Sunny:
                    return "sunny";
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
