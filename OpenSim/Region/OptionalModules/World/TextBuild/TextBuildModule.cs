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
            if (chat == null || chat.Sender == null || chat.Channel != m_commandChannel)
                return;

            string request = chat.Message == null ? string.Empty : chat.Message.Trim();
            if (!IsBuildCommand(request))
                return;

            IClientAPI client = chat.Sender;
            if (m_estateManagerOnly && !m_scene.Permissions.IsEstateManager(client.AgentId))
            {
                SendReply(client, "TextBuild: only estate managers can use automatic building here.");
                return;
            }

            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsChildAgent)
                return;

            BuildTemplate template = ResolveTemplate(request);
            if (template == null)
            {
                SendReply(client, "TextBuild: I can build car, house, tree, dock, table.");
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
            string lower = request.ToLower(CultureInfo.InvariantCulture);
            return lower.StartsWith("build ")
                || lower.StartsWith("create ")
                || lower.StartsWith("make ")
                || lower.StartsWith("costruisci ")
                || lower.StartsWith("costruiscimi ")
                || lower.StartsWith("crea ");
        }

        private static BuildTemplate ResolveTemplate(string request)
        {
            string lower = request.ToLower(CultureInfo.InvariantCulture);

            if (lower.Contains("car") || lower.Contains("machine") || lower.Contains("macchina") || lower.Contains("auto"))
                return CreateCarTemplate();

            if (lower.Contains("house") || lower.Contains("home") || lower.Contains("casa"))
                return CreateHouseTemplate();

            if (lower.Contains("tree") || lower.Contains("albero"))
                return CreateTreeTemplate();

            if (lower.Contains("dock") || lower.Contains("pier") || lower.Contains("molo") || lower.Contains("pontile"))
                return CreateDockTemplate();

            if (lower.Contains("table") || lower.Contains("tavolo"))
                return CreateTableTemplate();

            return null;
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
            else
                shape = PrimitiveBaseShape.CreateBox();

            shape.Scale = buildPart.Scale;
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
            return new BuildTemplate("textbuild car", 0.35f,
                Box("car body", new Vector3(0f, 0f, 0.45f), new Vector3(3.2f, 1.5f, 0.55f), new Color4(0.1f, 0.28f, 0.75f, 1f)),
                Box("car cabin", new Vector3(0.1f, 0f, 0.95f), new Vector3(1.35f, 1.15f, 0.55f), new Color4(0.08f, 0.1f, 0.16f, 1f)),
                Cylinder("front left wheel", new Vector3(0.95f, 0.82f, 0.22f), new Vector3(0.38f, 0.38f, 0.28f), wheelRot, new Color4(0.02f, 0.02f, 0.02f, 1f)),
                Cylinder("front right wheel", new Vector3(0.95f, -0.82f, 0.22f), new Vector3(0.38f, 0.38f, 0.28f), wheelRot, new Color4(0.02f, 0.02f, 0.02f, 1f)),
                Cylinder("rear left wheel", new Vector3(-0.95f, 0.82f, 0.22f), new Vector3(0.38f, 0.38f, 0.28f), wheelRot, new Color4(0.02f, 0.02f, 0.02f, 1f)),
                Cylinder("rear right wheel", new Vector3(-0.95f, -0.82f, 0.22f), new Vector3(0.38f, 0.38f, 0.28f), wheelRot, new Color4(0.02f, 0.02f, 0.02f, 1f)));
        }

        private static BuildTemplate CreateHouseTemplate()
        {
            Quaternion roofRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Math.PI * 0.25f);
            return new BuildTemplate("textbuild house", 0.5f,
                Box("house body", new Vector3(0f, 0f, 1.0f), new Vector3(3.4f, 3.0f, 2.0f), new Color4(0.78f, 0.72f, 0.62f, 1f)),
                Box("house roof", new Vector3(0f, 0f, 2.25f), new Vector3(3.8f, 3.25f, 0.42f), roofRot, new Color4(0.55f, 0.12f, 0.08f, 1f)),
                Box("house door", new Vector3(1.72f, 0f, 0.65f), new Vector3(0.08f, 0.75f, 1.2f), new Color4(0.32f, 0.18f, 0.08f, 1f)),
                Box("left window", new Vector3(1.73f, 0.9f, 1.3f), new Vector3(0.06f, 0.55f, 0.45f), new Color4(0.45f, 0.75f, 0.95f, 1f)),
                Box("right window", new Vector3(1.73f, -0.9f, 1.3f), new Vector3(0.06f, 0.55f, 0.45f), new Color4(0.45f, 0.75f, 0.95f, 1f)));
        }

        private static BuildTemplate CreateTreeTemplate()
        {
            return new BuildTemplate("textbuild tree", 0.45f,
                Cylinder("tree trunk", new Vector3(0f, 0f, 1.0f), new Vector3(0.45f, 0.45f, 2.0f), Quaternion.Identity, new Color4(0.32f, 0.17f, 0.07f, 1f)),
                Sphere("tree crown", new Vector3(0f, 0f, 2.45f), new Vector3(2.2f, 2.2f, 1.8f), new Color4(0.08f, 0.45f, 0.14f, 1f)),
                Sphere("tree crown left", new Vector3(0f, 0.7f, 2.0f), new Vector3(1.35f, 1.35f, 1.15f), new Color4(0.06f, 0.36f, 0.12f, 1f)),
                Sphere("tree crown right", new Vector3(0f, -0.7f, 2.0f), new Vector3(1.35f, 1.35f, 1.15f), new Color4(0.06f, 0.36f, 0.12f, 1f)));
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
            return new BuildPart(name, BuildShape.Box, offset, scale, rotation, color);
        }

        private static BuildPart Sphere(string name, Vector3 offset, Vector3 scale, Color4 color)
        {
            return new BuildPart(name, BuildShape.Sphere, offset, scale, Quaternion.Identity, color);
        }

        private static BuildPart Cylinder(string name, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color)
        {
            return new BuildPart(name, BuildShape.Cylinder, offset, scale, rotation, color);
        }

        private enum BuildShape
        {
            Box,
            Sphere,
            Cylinder
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

            public BuildPart(string name, BuildShape shape, Vector3 offset, Vector3 scale, Quaternion rotation, Color4 color)
            {
                Name = name;
                Shape = shape;
                Offset = offset;
                Scale = scale;
                Rotation = rotation;
                Color = color;
            }
        }
    }
}
