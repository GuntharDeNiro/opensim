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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Threading;
using CSJ2K;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.Imaging;
using OpenMetaverse.Rendering;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.LegacyMap
{
    public enum DrawRoutine
    {
        Rectangle,
        Polygon,
        Ellipse
    }

    public struct face
    {
        public Point[] pts;
    }

    public struct DrawStruct
    {
        public DrawRoutine dr;
//        public Rectangle rect;
        public SolidBrush brush;
        public SolidBrush[] faceBrushes;
        public SolidBrush shadowBrush;
        public Pen outlinePen;
        public face[] trns;
    }

    public struct MapPolygonDraw
    {
        public Point[] points;
        public SolidBrush brush;
        public Pen outlinePen;
        public float z;
    }

    public struct MapTextureSample
    {
        public bool valid;
        public Color color;
        public float alpha;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MapImageModule")]
    public class MapImageModule : IMapImageGenerator, INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private IConfigSource m_config;
        private IMapTileTerrainRenderer terrainRenderer;
        private IRendering m_primMesher;
        private bool m_Enabled = false;
        private static readonly string[] MapConfigSections = new string[] { "Map", "Startup" };
        private readonly Dictionary<UUID, MapTextureSample> m_textureSampleCache = new Dictionary<UUID, MapTextureSample>();
        private readonly Dictionary<string, FacetedMesh> m_renderMeshCache = new Dictionary<string, FacetedMesh>();
        private readonly HashSet<string> m_failedRenderMeshCache = new HashSet<string>();
        private int m_textureAssetSamplesThisPass = 0;
        private int m_maxTextureAssetSamplesThisPass = 0;

        #region IMapImageGenerator Members

        public Bitmap CreateMapTile()
        {
            bool drawPrimVolume = true;
            bool textureTerrain = false;
            bool generateMaptiles = true;
            Bitmap mapbmp;

            drawPrimVolume
                = Util.GetConfigVarFromSections<bool>(m_config, "DrawPrimOnMapTile", MapConfigSections, drawPrimVolume);
            textureTerrain
                = Util.GetConfigVarFromSections<bool>(m_config, "TextureOnMapTile", MapConfigSections, textureTerrain);
            generateMaptiles
                = Util.GetConfigVarFromSections<bool>(m_config, "GenerateMaptiles", MapConfigSections, generateMaptiles);

            if (generateMaptiles)
            {
                if (String.IsNullOrEmpty(m_scene.RegionInfo.MaptileStaticFile))
                {
                    if (textureTerrain)
                    {
                        terrainRenderer = new TexturedMapTileRenderer();
                    }
                    else
                    {
                        terrainRenderer = new ShadedMapTileRenderer();
                    }

                    terrainRenderer.Initialise(m_scene, m_config);

                    mapbmp = new Bitmap((int)m_scene.Heightmap.Width, (int)m_scene.Heightmap.Height,
                                            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    //long t = System.Environment.TickCount;
                    //for (int i = 0; i < 10; ++i) {
                    terrainRenderer.TerrainToBitmap(mapbmp);
                    //}
                    //t = System.Environment.TickCount - t;
                    //m_log.InfoFormat("[MAPTILE] generation of 10 maptiles needed {0} ms", t);
                    if (drawPrimVolume)
                    {
                        DrawObjectVolume(m_scene, mapbmp);
                    }

                    if (Util.GetConfigVarFromSections<bool>(m_config,
                        "MapTileAerialStyle", MapConfigSections, true))
                    {
                        ApplyAerialMapStyle(mapbmp,
                            Util.GetConfigVarFromSections<float>(m_config,
                                "MapTileAerialSoften", MapConfigSections, 0.08f),
                            Util.GetConfigVarFromSections<float>(m_config,
                                "MapTileAerialSaturation", MapConfigSections, 0.92f),
                            Util.GetConfigVarFromSections<float>(m_config,
                                "MapTileAerialContrast", MapConfigSections, 1.04f),
                            Util.GetConfigVarFromSections<int>(m_config,
                                "MapTileAerialBrightness", MapConfigSections, 1),
                            Util.GetConfigVarFromSections<float>(m_config,
                                "MapTileAerialSharpen", MapConfigSections, 0.24f));
                    }
                }
                else
                {
                    try
                    {
                        mapbmp = new Bitmap(m_scene.RegionInfo.MaptileStaticFile);
                    }
                    catch (Exception)
                    {
                        m_log.ErrorFormat(
                            "[MAPTILE]: Failed to load Static map image texture file: {0} for {1}",
                            m_scene.RegionInfo.MaptileStaticFile, m_scene.Name);
                        //mapbmp = new Bitmap((int)m_scene.Heightmap.Width, (int)m_scene.Heightmap.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                        mapbmp = null;
                    }

                    if (mapbmp != null)
                        m_log.DebugFormat(
                            "[MAPTILE]: Static map image texture file {0} found for {1}",
                            m_scene.RegionInfo.MaptileStaticFile, m_scene.Name);
                }
            }
            else
            {
                mapbmp = FetchTexture(m_scene.RegionInfo.RegionSettings.TerrainImageID);
            }

            return mapbmp;
        }

        public byte[] WriteJpeg2000Image()
        {
            try
            {
                using (Bitmap mapbmp = CreateMapTile())
                {
                    if (mapbmp != null)
                        return OpenJPEG.EncodeFromImage(mapbmp, false);
                }
            }
            catch (Exception e) // LEGIT: Catching problems caused by OpenJPEG p/invoke
            {
                m_log.Error("Failed generating terrain map: " + e);
            }

            return null;
        }

        #endregion

        #region Region Module interface

        public void Initialise(IConfigSource source)
        {
            m_config = source;

            if (Util.GetConfigVarFromSections<string>(
                m_config, "MapImageModule", new string[] { "Startup", "Map" }, "MapImageModule") != "MapImageModule")
                return;

            m_Enabled = true;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;

            m_scene.RegisterModuleInterface<IMapImageGenerator>(this);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "MapImageModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

// TODO: unused:
//         private void ShadeBuildings(Bitmap map)
//         {
//             lock (map)
//             {
//                 lock (m_scene.Entities)
//                 {
//                     foreach (EntityBase entity in m_scene.Entities.Values)
//                     {
//                         if (entity is SceneObjectGroup)
//                         {
//                             SceneObjectGroup sog = (SceneObjectGroup) entity;
//
//                             foreach (SceneObjectPart primitive in sog.Children.Values)
//                             {
//                                 int x = (int) (primitive.AbsolutePosition.X - (primitive.Scale.X / 2));
//                                 int y = (int) (primitive.AbsolutePosition.Y - (primitive.Scale.Y / 2));
//                                 int w = (int) primitive.Scale.X;
//                                 int h = (int) primitive.Scale.Y;
//
//                                 int dx;
//                                 for (dx = x; dx < x + w; dx++)
//                                 {
//                                     int dy;
//                                     for (dy = y; dy < y + h; dy++)
//                                     {
//                                         if (x < 0 || y < 0)
//                                             continue;
//                                         if (x >= map.Width || y >= map.Height)
//                                             continue;
//
//                                         map.SetPixel(dx, dy, Color.DarkGray);
//                                     }
//                                 }
//                             }
//                         }
//                     }
//                 }
//             }
//         }

        private Bitmap FetchTexture(UUID id)
        {
            AssetBase asset = m_scene.AssetService.Get(id.ToString());

            if (asset != null)
            {
                m_log.DebugFormat("[MAPTILE]: Static map image texture {0} found for {1}", id, m_scene.Name);
            }
            else
            {
                m_log.WarnFormat("[MAPTILE]: Static map image texture {0} not found for {1}", id, m_scene.Name);
                return null;
            }

            try
            {
                using (Image image = DecodeMapImage(asset.Data))
                {
                    if (image != null)
                        return new Bitmap(image);
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[MAPTILE]: Static map image texture {0} could not be decoded for {1}: {2}",
                    id, m_scene.Name, e.Message);
            }

            return null;

        }

        private Bitmap DrawObjectVolume(Scene whichScene, Bitmap mapbmp)
        {
            int tc = 0;
            ITerrainChannel hm = whichScene.Heightmap;
            tc = Environment.TickCount;
            m_log.Debug("[MAPTILE]: Generating Maptile Step 2: Object Volume Profile");
            EntityBase[] objs = whichScene.GetEntities();
            List<float> z_sortheights = new List<float>();
            List<uint> z_localIDs = new List<uint>();
            Dictionary<uint, DrawStruct> z_sort = new Dictionary<uint, DrawStruct>();
            List<MapPolygonDraw> meshGeometry = new List<MapPolygonDraw>();
            int yieldCounter = 0;
            int lastYieldMS = Environment.TickCount;
            bool prettyObjectVolume = Util.GetConfigVarFromSections<bool>(
                m_config, "PrettyPrimVolumeOnMapTile", MapConfigSections, true);
            bool drawObjectOutlines = Util.GetConfigVarFromSections<bool>(
                m_config, "MapObjectVolumeOutlines", MapConfigSections, true);
            bool drawObjectShadows = Util.GetConfigVarFromSections<bool>(
                m_config, "MapObjectVolumeShadows", MapConfigSections, true);
            int shadowOpacity = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeShadowOpacity", MapConfigSections, 24));
            int shadowOffset = Math.Max(0, Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeShadowOffset", MapConfigSections, 2));
            int objectOpacity = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeOpacity", MapConfigSections, 175));
            int largeObjectOpacity = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeLargeOpacity", MapConfigSections, 120));
            int outlineOpacity = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeOutlineOpacity", MapConfigSections, 85));
            bool useTextureAlpha = Util.GetConfigVarFromSections<bool>(
                m_config, "MapObjectVolumeUseTextureAlpha", MapConfigSections, true);
            int minimumOpacity = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMinimumOpacity", MapConfigSections, 35));
            int minimumBrightness = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMinimumBrightness", MapConfigSections, 72));
            int maximumBrightness = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMaximumBrightness", MapConfigSections, 235));
            int largeObjectArea = Math.Max(1, Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeLargeArea", MapConfigSections, 1800));
            bool sampleTextureAssets = Util.GetConfigVarFromSections<bool>(
                m_config, "MapObjectVolumeSampleTextureAssets", MapConfigSections, true);
            float textureBlend = Math.Max(0f, Math.Min(1f, Util.GetConfigVarFromSections<float>(
                m_config, "MapObjectVolumeTextureBlend", MapConfigSections, 0.90f)));
            float objectSaturation = Math.Max(0f, Math.Min(2f, Util.GetConfigVarFromSections<float>(
                m_config, "MapObjectVolumeColorSaturation", MapConfigSections, 1.10f)));
            float objectContrast = Math.Max(0f, Math.Min(2f, Util.GetConfigVarFromSections<float>(
                m_config, "MapObjectVolumeColorContrast", MapConfigSections, 1.08f)));
            bool faceShading = Util.GetConfigVarFromSections<bool>(
                m_config, "MapObjectVolumeFaceShading", MapConfigSections, true);
            bool renderMeshGeometry = true;
            DetailLevel meshDetailLevel = GetMapMeshDetailLevel(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMeshDetailLevel", MapConfigSections, 2));
            m_maxTextureAssetSamplesThisPass = Math.Max(0, Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMaxTextureSamples", MapConfigSections, 0));
            m_textureAssetSamplesThisPass = 0;
            int geometryFailures = 0;

            EnsurePrimMesher(renderMeshGeometry);

            try
            {
                lock (objs)
                {
                    foreach (EntityBase obj in objs)
                    {
                        // Only draw the contents of SceneObjectGroup
                        if (obj is SceneObjectGroup)
                        {
                            SceneObjectGroup mapdot = (SceneObjectGroup)obj;
                            // Loop over prim in group
                            foreach (SceneObjectPart part in mapdot.Parts)
                            {
                                YieldMaptileWork(ref yieldCounter, ref lastYieldMS);

                                if (part == null)
                                    continue;

                                // Draw every real object part. Exact geometry handles small parts without box inflation.
                                if (part.Scale.X > 0f && part.Scale.Y > 0f && part.Scale.Z > 0f)
                                {
                                    Color mapdotspot = Color.Gray; // Default color when prim color is white
                                    // Try to get the RGBA of the default texture entry..
                                    //
                                    try
                                    {
                                        // get the null checks out of the way
                                        // skip the ones that break
                                        if (part == null)
                                            continue;

                                        if (part.Shape == null)
                                            continue;

                                        mapdotspot = GetPartMapColor(part, mapdotspot, prettyObjectVolume,
                                            sampleTextureAssets,
                                            minimumBrightness, maximumBrightness,
                                            textureBlend, objectSaturation, objectContrast);
                                    }
                                    catch (IndexOutOfRangeException)
                                    {
                                        // Windows Array
                                    }
                                    catch (ArgumentOutOfRangeException)
                                    {
                                        // Mono Array
                                    }

                                    Vector3 pos = part.GetWorldPosition();

                                    // skip prim outside of region
                                    if (!m_scene.PositionIsInCurrentRegion(pos))
                                        continue;

                                    // skip prim in non-finite position
                                    if (Single.IsNaN(pos.X) || Single.IsNaN(pos.Y) ||
                                        Single.IsInfinity(pos.X) || Single.IsInfinity(pos.Y))
                                        continue;

                                    // Figure out if object is under 256m above the height of the terrain
                                    bool isBelow256AboveTerrain = false;

                                    try
                                    {
                                        isBelow256AboveTerrain = (pos.Z < ((float)hm[(int)pos.X, (int)pos.Y] + 256f));
                                    }
                                    catch (Exception)
                                    {
                                    }

                                    if (isBelow256AboveTerrain)
                                    {
                                        // Translate scale by rotation so scale is represented properly when object is rotated
                                        Vector3 lscale = new Vector3(part.Shape.Scale.X, part.Shape.Scale.Y, part.Shape.Scale.Z);
                                        lscale *= 0.5f;

                                        Vector3 scale = new Vector3();
                                        Vector3 tScale = new Vector3();
                                        Vector3 axPos = new Vector3(pos.X, pos.Y, pos.Z);

                                        Quaternion rot = part.GetWorldRotation();
                                        scale = lscale * rot;

                                        // negative scales don't work in this situation
                                        scale.X = Math.Abs(scale.X);
                                        scale.Y = Math.Abs(scale.Y);
                                        scale.Z = Math.Abs(scale.Z);

                                        // This scaling isn't very accurate and doesn't take into account the face rotation :P
                                        int mapdrawstartX = (int)(pos.X - scale.X);
                                        int mapdrawstartY = (int)(pos.Y - scale.Y);
                                        int mapdrawendX = (int)(pos.X + scale.X);
                                        int mapdrawendY = (int)(pos.Y + scale.Y);
                                        int objectArea = Math.Abs(mapdrawendX - mapdrawstartX) * Math.Abs(mapdrawendY - mapdrawstartY);
                                        int fillOpacity = prettyObjectVolume && objectArea >= largeObjectArea
                                            ? largeObjectOpacity
                                            : objectOpacity;
                                        float textureAlpha = 1f;
                                        if (prettyObjectVolume && useTextureAlpha)
                                        {
                                            textureAlpha = GetPartTextureAlpha(part,
                                                sampleTextureAssets);
                                            fillOpacity = ApplyTextureAlpha(fillOpacity,
                                                textureAlpha,
                                                minimumOpacity);
                                        }

                                        if (renderMeshGeometry)
                                        {
                                            if (TryAddMeshGeometryDraws(meshGeometry, part, mapdotspot,
                                                    fillOpacity, outlineOpacity, drawObjectOutlines,
                                                    faceShading, meshDetailLevel))
                                            {
                                                continue;
                                            }

                                            geometryFailures++;
                                            continue;
                                        }

                                        // If object is beyond the edge of the map, don't draw it to avoid errors
                                        if (mapdrawstartX < 0
                                                    || mapdrawstartX > (hm.Width - 1)
                                                    || mapdrawendX < 0
                                                    || mapdrawendX > (hm.Width - 1)
                                                    || mapdrawstartY < 0
                                                    || mapdrawstartY > (hm.Height - 1)
                                                    || mapdrawendY < 0
                                                    || mapdrawendY > (hm.Height - 1))
                                            continue;

                                        #region obb face reconstruction part duex
                                        Vector3[] vertexes = new Vector3[8];

                                        // float[] distance = new float[6];
                                        Vector3[] FaceA = new Vector3[6]; // vertex A for Facei
                                        Vector3[] FaceB = new Vector3[6]; // vertex B for Facei
                                        Vector3[] FaceC = new Vector3[6]; // vertex C for Facei
                                        Vector3[] FaceD = new Vector3[6]; // vertex D for Facei

                                        tScale = new Vector3(lscale.X, -lscale.Y, lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[0] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));
                                        // vertexes[0].x = pos.X + vertexes[0].x;
                                        //vertexes[0].y = pos.Y + vertexes[0].y;
                                        //vertexes[0].z = pos.Z + vertexes[0].z;

                                        FaceA[0] = vertexes[0];
                                        FaceB[3] = vertexes[0];
                                        FaceA[4] = vertexes[0];

                                        tScale = lscale;
                                        scale = tScale * rot;
                                        vertexes[1] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        // vertexes[1].x = pos.X + vertexes[1].x;
                                        // vertexes[1].y = pos.Y + vertexes[1].y;
                                        //vertexes[1].z = pos.Z + vertexes[1].z;

                                        FaceB[0] = vertexes[1];
                                        FaceA[1] = vertexes[1];
                                        FaceC[4] = vertexes[1];

                                        tScale = new Vector3(lscale.X, -lscale.Y, -lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[2] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        //vertexes[2].x = pos.X + vertexes[2].x;
                                        //vertexes[2].y = pos.Y + vertexes[2].y;
                                        //vertexes[2].z = pos.Z + vertexes[2].z;

                                        FaceC[0] = vertexes[2];
                                        FaceD[3] = vertexes[2];
                                        FaceC[5] = vertexes[2];

                                        tScale = new Vector3(lscale.X, lscale.Y, -lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[3] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        //vertexes[3].x = pos.X + vertexes[3].x;
                                        // vertexes[3].y = pos.Y + vertexes[3].y;
                                        // vertexes[3].z = pos.Z + vertexes[3].z;

                                        FaceD[0] = vertexes[3];
                                        FaceC[1] = vertexes[3];
                                        FaceA[5] = vertexes[3];

                                        tScale = new Vector3(-lscale.X, lscale.Y, lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[4] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        // vertexes[4].x = pos.X + vertexes[4].x;
                                        // vertexes[4].y = pos.Y + vertexes[4].y;
                                        // vertexes[4].z = pos.Z + vertexes[4].z;

                                        FaceB[1] = vertexes[4];
                                        FaceA[2] = vertexes[4];
                                        FaceD[4] = vertexes[4];

                                        tScale = new Vector3(-lscale.X, lscale.Y, -lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[5] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        // vertexes[5].x = pos.X + vertexes[5].x;
                                        // vertexes[5].y = pos.Y + vertexes[5].y;
                                        // vertexes[5].z = pos.Z + vertexes[5].z;

                                        FaceD[1] = vertexes[5];
                                        FaceC[2] = vertexes[5];
                                        FaceB[5] = vertexes[5];

                                        tScale = new Vector3(-lscale.X, -lscale.Y, lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[6] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        // vertexes[6].x = pos.X + vertexes[6].x;
                                        // vertexes[6].y = pos.Y + vertexes[6].y;
                                        // vertexes[6].z = pos.Z + vertexes[6].z;

                                        FaceB[2] = vertexes[6];
                                        FaceA[3] = vertexes[6];
                                        FaceB[4] = vertexes[6];

                                        tScale = new Vector3(-lscale.X, -lscale.Y, -lscale.Z);
                                        scale = tScale * rot;
                                        vertexes[7] = (new Vector3((pos.X + scale.X), (pos.Y + scale.Y), (pos.Z + scale.Z)));

                                        // vertexes[7].x = pos.X + vertexes[7].x;
                                        // vertexes[7].y = pos.Y + vertexes[7].y;
                                        // vertexes[7].z = pos.Z + vertexes[7].z;

                                        FaceD[2] = vertexes[7];
                                        FaceC[3] = vertexes[7];
                                        FaceD[5] = vertexes[7];
                                        #endregion

                                        //int wy = 0;

                                        //bool breakYN = false; // If we run into an error drawing, break out of the
                                        // loop so we don't lag to death on error handling
                                        DrawStruct ds = new DrawStruct();
                                        ds.brush = new SolidBrush(prettyObjectVolume
                                            ? Color.FromArgb(fillOpacity, mapdotspot)
                                            : mapdotspot);
                                        ds.faceBrushes = null;
                                        ds.shadowBrush = prettyObjectVolume && drawObjectShadows && shadowOpacity > 0
                                            ? new SolidBrush(Color.FromArgb(shadowOpacity, 18, 22, 22))
                                            : null;
                                        ds.outlinePen = null;
                                        if (prettyObjectVolume && drawObjectOutlines)
                                            ds.outlinePen = new Pen(Color.FromArgb(outlineOpacity, Darken(mapdotspot, 0.55f)), 1f);
                                        //ds.rect = new Rectangle(mapdrawstartX, (255 - mapdrawstartY), mapdrawendX - mapdrawstartX, mapdrawendY - mapdrawstartY);

                                        ds.trns = new face[FaceA.Length];
                                        if (prettyObjectVolume && faceShading)
                                            ds.faceBrushes = new SolidBrush[FaceA.Length];

                                        for (int i = 0; i < FaceA.Length; i++)
                                        {
                                            Point[] working = new Point[5];
                                            working[0] = project(hm, FaceA[i], axPos);
                                            working[1] = project(hm, FaceB[i], axPos);
                                            working[2] = project(hm, FaceD[i], axPos);
                                            working[3] = project(hm, FaceC[i], axPos);
                                            working[4] = project(hm, FaceA[i], axPos);

                                            face workingface = new face();
                                            workingface.pts = working;

                                            ds.trns[i] = workingface;
                                            if (ds.faceBrushes != null)
                                            {
                                                Color shaded = ShadeFaceColor(mapdotspot, FaceA[i], FaceB[i], FaceC[i]);
                                                ds.faceBrushes[i] = new SolidBrush(Color.FromArgb(fillOpacity, shaded));
                                            }
                                        }

                                        z_sort.Add(part.LocalId, ds);
                                        z_localIDs.Add(part.LocalId);
                                        z_sortheights.Add(pos.Z);

                                        // for (int wx = mapdrawstartX; wx < mapdrawendX; wx++)
                                        // {
                                        //     for (wy = mapdrawstartY; wy < mapdrawendY; wy++)
                                        //     {
                                        //         m_log.InfoFormat("[MAPDEBUG]: {0},{1}({2})", wx, (255 - wy),wy);
                                        //         try
                                        //         {
                                        //             // Remember, flip the y!
                                        //             mapbmp.SetPixel(wx, (255 - wy), mapdotspot);
                                        //         }
                                        //         catch (ArgumentException)
                                        //         {
                                        //             breakYN = true;
                                        //         }
                                        //     }
                                        //     if (breakYN)
                                        //         break;
                                        //     }
                                        // }
                                        //}
                                    } // Object is within 256m Z of terrain
                                } // object is at least a meter wide
                            } // loop over group children
                        } // entitybase is sceneobject group
                    } // foreach loop over entities

                    float[] sortedZHeights = z_sortheights.ToArray();
                    uint[] sortedlocalIds = z_localIDs.ToArray();

                    if (geometryFailures > 0)
                    {
                        m_log.DebugFormat(
                            "[MAPTILE]: {0} object parts could not be drawn because exact geometry was unavailable",
                            geometryFailures);
                    }

                    // Sort prim by Z position
                    Array.Sort(sortedZHeights, sortedlocalIds);

                    using (Graphics g = Graphics.FromImage(mapbmp))
                    {
                        if (prettyObjectVolume)
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                        }

                        for (int s = 0; s < sortedZHeights.Length; s++)
                        {
                            YieldMaptileWork(ref yieldCounter, ref lastYieldMS);

                            if (z_sort.ContainsKey(sortedlocalIds[s]))
                            {
                                DrawStruct rectDrawStruct = z_sort[sortedlocalIds[s]];
                                for (int r = 0; r < rectDrawStruct.trns.Length; r++)
                                {
                                    if (rectDrawStruct.shadowBrush != null && shadowOffset > 0)
                                        g.FillPolygon(rectDrawStruct.shadowBrush,
                                            OffsetPoints(rectDrawStruct.trns[r].pts, shadowOffset, shadowOffset));
                                }
                            }
                        }

                        meshGeometry.Sort(delegate(MapPolygonDraw left, MapPolygonDraw right)
                        {
                            return left.z.CompareTo(right.z);
                        });

                        foreach (MapPolygonDraw mesh in meshGeometry)
                        {
                            YieldMaptileWork(ref yieldCounter, ref lastYieldMS);

                            g.FillPolygon(mesh.brush, mesh.points);
                            if (mesh.outlinePen != null)
                                g.DrawPolygon(mesh.outlinePen, mesh.points);
                        }

                        for (int s = 0; s < sortedZHeights.Length; s++)
                        {
                            YieldMaptileWork(ref yieldCounter, ref lastYieldMS);

                            if (z_sort.ContainsKey(sortedlocalIds[s]))
                            {
                                DrawStruct rectDrawStruct = z_sort[sortedlocalIds[s]];
                                for (int r = 0; r < rectDrawStruct.trns.Length; r++)
                                {
                                    SolidBrush fillBrush = rectDrawStruct.faceBrushes != null &&
                                        r < rectDrawStruct.faceBrushes.Length &&
                                        rectDrawStruct.faceBrushes[r] != null
                                        ? rectDrawStruct.faceBrushes[r]
                                        : rectDrawStruct.brush;
                                    g.FillPolygon(fillBrush,rectDrawStruct.trns[r].pts);
                                    if (rectDrawStruct.outlinePen != null)
                                        g.DrawPolygon(rectDrawStruct.outlinePen, rectDrawStruct.trns[r].pts);
                                }
                                //g.FillRectangle(rectDrawStruct.brush , rectDrawStruct.rect);
                            }
                        }
                    }
                } // lock entities objs

            }
            finally
            {
                foreach (DrawStruct ds in z_sort.Values)
                {
                    ds.brush.Dispose();
                    if (ds.faceBrushes != null)
                    {
                        foreach (SolidBrush brush in ds.faceBrushes)
                        {
                            if (brush != null)
                                brush.Dispose();
                        }
                    }
                    if (ds.shadowBrush != null)
                        ds.shadowBrush.Dispose();
                    if (ds.outlinePen != null)
                        ds.outlinePen.Dispose();
                }

                foreach (MapPolygonDraw mesh in meshGeometry)
                {
                    mesh.brush.Dispose();
                    if (mesh.outlinePen != null)
                        mesh.outlinePen.Dispose();
                }
            }

            m_log.Debug("[MAPTILE]: Generating Maptile Step 2: Done in " + (Environment.TickCount - tc) + " ms");

            return mapbmp;
        }

        private void EnsurePrimMesher(bool needed)
        {
            if (!needed || m_primMesher != null)
                return;

            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
            {
                m_primMesher = RenderingLoader.LoadRenderer(renderers[0]);
                m_log.Debug("[MAPTILE]: Loaded mesh geometry renderer " + renderers[0]);
            }
            else
            {
                m_log.Warn("[MAPTILE]: No mesh geometry renderer available; object map tiles cannot draw exact object geometry");
            }
        }

        private static DetailLevel GetMapMeshDetailLevel(int configuredLevel)
        {
            if (configuredLevel < 0)
                configuredLevel = 0;
            else if (configuredLevel > 3)
                configuredLevel = 3;

            return (DetailLevel)configuredLevel;
        }

        private static void YieldMaptileWork(ref int counter, ref int lastYieldMS)
        {
            counter++;
            if ((counter & 0x7f) != 0)
                return;

            int now = Environment.TickCount;
            if (Util.EnvironmentTickCountSubtract(now, lastYieldMS) < 20)
                return;

            Thread.Sleep(1);
            lastYieldMS = Environment.TickCount;
        }

        private bool TryAddMeshGeometryDraws(List<MapPolygonDraw> meshGeometry, SceneObjectPart part,
            Color fallbackColor, int opacity, int outlineOpacity, bool drawOutlines,
            bool faceShading, DetailLevel lod)
        {
            if (m_primMesher == null)
                return false;

            FacetedMesh renderMesh = GetRenderMesh(part, lod);
            if (renderMesh == null || renderMesh.Faces == null || renderMesh.Faces.Count == 0)
                return false;

            Primitive.TextureEntry textureEntry = part.Shape.Textures;
            if (textureEntry == null)
                return false;

            Vector3 pos = part.GetWorldPosition();
            Quaternion rot = part.GetWorldRotation();
            Vector3 scale = part.Scale;
            bool added = false;
            int yieldCounter = 0;
            int lastYieldMS = Environment.TickCount;

            for (int i = 0; i < renderMesh.Faces.Count; i++)
            {
                YieldMaptileWork(ref yieldCounter, ref lastYieldMS);

                Face face = renderMesh.Faces[i];
                if (face.Vertices == null || face.Indices == null)
                    continue;

                Primitive.TextureEntryFace textureFace = textureEntry.GetFace((uint)i);
                if (textureFace == null)
                    textureFace = textureEntry.DefaultTexture;
                if (textureFace == null || textureFace.RGBA.A <= 0f)
                    continue;

                Color faceColor = GetTextureFaceMapColor(textureFace, fallbackColor);
                int faceOpacity = ApplyTextureAlpha(opacity, textureFace.RGBA.A, 0);

                for (int j = 0; j + 2 < face.Indices.Count; j += 3)
                {
                    YieldMaptileWork(ref yieldCounter, ref lastYieldMS);

                    int index0 = face.Indices[j];
                    int index1 = face.Indices[j + 1];
                    int index2 = face.Indices[j + 2];

                    if (index0 < 0 || index1 < 0 || index2 < 0 ||
                        index0 >= face.Vertices.Count ||
                        index1 >= face.Vertices.Count ||
                        index2 >= face.Vertices.Count)
                        continue;

                    Vector3 world0 = MeshVertexToWorld(face.Vertices[index0].Position, scale, rot, pos);
                    Vector3 world1 = MeshVertexToWorld(face.Vertices[index1].Position, scale, rot, pos);
                    Vector3 world2 = MeshVertexToWorld(face.Vertices[index2].Position, scale, rot, pos);

                    if (!MapTriangleTouchesMap(world0, world1, world2))
                        continue;

                    Color drawColor = faceShading ? ShadeFaceColor(faceColor, world0, world1, world2) : faceColor;
                    Point[] points = new Point[]
                    {
                        WorldToMapPoint(world0),
                        WorldToMapPoint(world1),
                        WorldToMapPoint(world2)
                    };

                    meshGeometry.Add(new MapPolygonDraw
                    {
                        points = points,
                        brush = new SolidBrush(Color.FromArgb(faceOpacity, drawColor)),
                        outlinePen = drawOutlines
                            ? new Pen(Color.FromArgb(Math.Min(outlineOpacity, 72), Darken(drawColor, 0.50f)), 1f)
                            : null,
                        z = (world0.Z + world1.Z + world2.Z) / 3f
                    });
                    added = true;
                }
            }

            return added;
        }

        private FacetedMesh GetRenderMesh(SceneObjectPart part, DetailLevel lod)
        {
            Primitive omvPrim = part.Shape.ToOmvPrimitive(part.OffsetPosition, part.RotationOffset);
            FacetedMesh renderMesh = null;

            if (omvPrim.Sculpt != null && omvPrim.Sculpt.SculptTexture.IsNotZero())
            {
                string cacheKey = omvPrim.Sculpt.SculptTexture + ":" + omvPrim.Sculpt.Type + ":" + lod;
                if (m_renderMeshCache.TryGetValue(cacheKey, out renderMesh))
                    return renderMesh;
                if (m_failedRenderMeshCache.Contains(cacheKey))
                    return null;

                string assetID = omvPrim.Sculpt.SculptTexture.ToString();
                byte[] sculptData = GetAssetDataForMap(assetID);
                if (sculptData != null && sculptData.Length > 0)
                {
                    try
                    {
                        if (omvPrim.Sculpt.Type == SculptType.Mesh)
                        {
                            AssetMesh meshAsset = new AssetMesh(omvPrim.Sculpt.SculptTexture, sculptData);
                            FacetedMesh.TryDecodeFromAsset(omvPrim, meshAsset, lod, out renderMesh);
                        }
                        else
                        {
                            Image sculpt = DecodeMapImage(sculptData);
                            if (sculpt != null)
                            {
                                using (sculpt)
                                {
                                    renderMesh = m_primMesher.GenerateFacetedSculptMesh(omvPrim, (Bitmap)sculpt, lod);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.DebugFormat("[MAPTILE]: Exact geometry decode failed for object '{0}' ({1}), asset {2}, sculpt type {3}: {4}",
                            part.Name, part.UUID, omvPrim.Sculpt.SculptTexture, omvPrim.Sculpt.Type, e.Message);
                    }

                    if (renderMesh != null)
                        m_renderMeshCache[cacheKey] = renderMesh;
                    else
                        m_failedRenderMeshCache.Add(cacheKey);
                }
                else
                {
                    m_log.DebugFormat("[MAPTILE]: Exact geometry asset missing for object '{0}' ({1}), asset {2}, sculpt type {3}",
                        part.Name, part.UUID, omvPrim.Sculpt.SculptTexture, omvPrim.Sculpt.Type);
                    m_failedRenderMeshCache.Add(cacheKey);
                }

                if (renderMesh == null)
                {
                    m_log.DebugFormat("[MAPTILE]: Object '{0}' ({1}) was not drawn because exact sculpt/mesh geometry was unavailable",
                        part.Name, part.UUID);
                }

                return renderMesh;
            }

            return m_primMesher.GenerateFacetedMesh(omvPrim, lod);
        }

        private static Image DecodeMapImage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            if (!LooksLikeCompleteJpeg2000(data))
                return null;

            try
            {
                // Map generation is background/diagnostic work.  Do not use the
                // native OpenJPEG path here: corrupted in-world texture assets can
                // raise AccessViolationException in native decode and take down the
                // whole simulator.  The managed CSJ2K decoder may fail, but it fails
                // as a normal exception that callers can recover from.
                return J2kImage.FromBytes(data, null, true, 12);
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeCompleteJpeg2000(byte[] data)
        {
            if (data.Length < 16)
                return false;

            bool rawCodestream = data[0] == 0xff && data[1] == 0x4f;
            bool jp2Container = data[0] == 0x00 && data[1] == 0x00 &&
                data[2] == 0x00 && data[3] == 0x0c &&
                data[4] == 0x6a && data[5] == 0x50 &&
                data[6] == 0x20 && data[7] == 0x20;

            if (!rawCodestream && !jp2Container)
                return false;

            for (int i = data.Length - 2; i >= 0; i--)
            {
                if (data[i] == 0xff && data[i + 1] == 0xd9)
                    return true;
            }

            return false;
        }

        private byte[] GetAssetDataForMap(string assetID)
        {
            try
            {
                AssetBase asset = m_scene.AssetService.Get(assetID);
                if (asset != null && asset.Data != null && asset.Data.Length > 0)
                    return asset.Data;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[MAPTILE]: AssetService.Get failed for map asset {0}: {1}", assetID, e.Message);
            }

            try
            {
                return m_scene.AssetService.GetData(assetID);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[MAPTILE]: AssetService.GetData failed for map asset {0}: {1}", assetID, e.Message);
                return null;
            }
        }

        private Color GetTextureFaceMapColor(Primitive.TextureEntryFace textureFace, Color fallback)
        {
            MapTextureSample sample = GetTextureFaceSample(textureFace);
            if (sample.valid)
                return sample.color;

            return fallback;
        }

        private static Vector3 MeshVertexToWorld(Vector3 vertex, Vector3 scale, Quaternion rot, Vector3 pos)
        {
            Vector3 local = new Vector3(vertex.X * scale.X, vertex.Y * scale.Y, vertex.Z * scale.Z);
            local *= rot;
            return pos + local;
        }

        private bool MapTriangleTouchesMap(Vector3 a, Vector3 b, Vector3 c)
        {
            if (!MapPointIsFinite(a) || !MapPointIsFinite(b) || !MapPointIsFinite(c))
                return false;

            float minX = Math.Min(a.X, Math.Min(b.X, c.X));
            float maxX = Math.Max(a.X, Math.Max(b.X, c.X));
            float minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            float maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));

            return maxX >= 0f && minX < m_scene.RegionInfo.RegionSizeX &&
                maxY >= 0f && minY < m_scene.RegionInfo.RegionSizeY;
        }

        private static bool MapPointIsFinite(Vector3 point)
        {
            return !Single.IsNaN(point.X) && !Single.IsNaN(point.Y) &&
                !Single.IsInfinity(point.X) && !Single.IsInfinity(point.Y);
        }

        private Point WorldToMapPoint(Vector3 point)
        {
            int regionHeight = (int)m_scene.RegionInfo.RegionSizeY;
            return new Point((int)point.X, (int)(regionHeight - 1 - point.Y));
        }

        private static Point[] OffsetPoints(Point[] source, int offsetX, int offsetY)
        {
            Point[] points = new Point[source.Length];
            for (int i = 0; i < source.Length; i++)
                points[i] = new Point(source[i].X + offsetX, source[i].Y + offsetY);
            return points;
        }

        private static void ApplyAerialMapStyle(Bitmap mapbmp, float softenBlend,
            float saturation, float contrast, int brightness, float sharpen)
        {
            softenBlend = Math.Max(0f, Math.Min(1f, softenBlend));
            saturation = Math.Max(0f, Math.Min(2f, saturation));
            contrast = Math.Max(0f, Math.Min(2f, contrast));
            sharpen = Math.Max(0f, Math.Min(1f, sharpen));

            using (Bitmap source = (Bitmap)mapbmp.Clone())
            {
                for (int y = 0; y < mapbmp.Height; y++)
                {
                    for (int x = 0; x < mapbmp.Width; x++)
                    {
                        Color center = source.GetPixel(x, y);
                        Color average = AverageNeighbourhood(source, x, y);
                        Color softened = Blend(center, average, softenBlend);
                        Color detailed = sharpen > 0f ? SharpenAgainstAverage(softened, average, sharpen) : softened;
                        mapbmp.SetPixel(x, y, AdjustAerialTone(detailed, saturation, contrast, brightness));
                    }
                }
            }
        }

        private static Color AverageNeighbourhood(Bitmap bitmap, int x, int y)
        {
            int r = 0;
            int g = 0;
            int b = 0;
            int count = 0;

            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(bitmap.Height - 1, y + 1); yy++)
            {
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(bitmap.Width - 1, x + 1); xx++)
                {
                    Color pixel = bitmap.GetPixel(xx, yy);
                    r += pixel.R;
                    g += pixel.G;
                    b += pixel.B;
                    count++;
                }
            }

            return Color.FromArgb(r / count, g / count, b / count);
        }

        private static Color AdjustAerialTone(Color color, float saturation, float contrast, int brightness)
        {
            float gray = (color.R * 0.299f) + (color.G * 0.587f) + (color.B * 0.114f);
            int r = ClampByte((int)((((gray + ((color.R - gray) * saturation)) - 128f) * contrast) + 128f + brightness));
            int g = ClampByte((int)((((gray + ((color.G - gray) * saturation)) - 128f) * contrast) + 128f + brightness));
            int b = ClampByte((int)((((gray + ((color.B - gray) * saturation)) - 128f) * contrast) + 128f + brightness));

            return Color.FromArgb(r, g, b);
        }

        private static Color SharpenAgainstAverage(Color color, Color average, float amount)
        {
            return Color.FromArgb(
                ClampByte((int)(color.R + ((color.R - average.R) * amount))),
                ClampByte((int)(color.G + ((color.G - average.G) * amount))),
                ClampByte((int)(color.B + ((color.B - average.B) * amount))));
        }

        private Color GetPartMapColor(SceneObjectPart part, Color fallback, bool prettyObjectVolume,
            bool sampleTextureAssets,
            int minimumBrightness, int maximumBrightness,
            float textureBlend, float saturation, float contrast)
        {
            Primitive.TextureEntry textureEntry = part.Shape.Textures;

            if (textureEntry == null || textureEntry.DefaultTexture == null)
                return fallback;

            Color4 texcolor = textureEntry.DefaultTexture.RGBA;

            int colorr = ClampByte((int)(texcolor.R * 255f));
            int colorg = ClampByte((int)(texcolor.G * 255f));
            int colorb = ClampByte((int)(texcolor.B * 255f));

            if (!prettyObjectVolume)
            {
                colorr = 255 - colorr;
                colorg = 255 - colorg;
                colorb = 255 - colorb;

                if (colorr == 255 && colorg == 255 && colorb == 255)
                    return fallback;

                return Color.FromArgb(colorr, colorg, colorb);
            }

            Color adjusted = Color.FromArgb(colorr, colorg, colorb);
            if (sampleTextureAssets)
            {
                MapTextureSample sample = GetPartTextureSample(part);
                if (sample.valid)
                    adjusted = Blend(adjusted, sample.color, textureBlend);
            }

            adjusted = AdjustAerialTone(adjusted, saturation, contrast, 0);
            adjusted = ClampBrightness(adjusted, minimumBrightness, maximumBrightness);

            if (adjusted.R > 246 && adjusted.G > 246 && adjusted.B > 246)
                adjusted = Color.FromArgb(224, 224, 216);

            return adjusted;
        }

        private float GetPartTextureAlpha(SceneObjectPart part, bool sampleTextureAssets)
        {
            return GetPartTextureAlpha(part, sampleTextureAssets, false);
        }

        private float GetPartTextureAlpha(SceneObjectPart part, bool sampleTextureAssets, bool prioritySample)
        {
            if (sampleTextureAssets)
            {
                MapTextureSample sample = GetPartTextureSample(part, prioritySample);
                if (sample.valid)
                    return sample.alpha;
            }

            Primitive.TextureEntry textureEntry = part.Shape.Textures;
            if (textureEntry == null || textureEntry.DefaultTexture == null)
                return 1f;

            return Math.Max(0f, Math.Min(1f, textureEntry.DefaultTexture.RGBA.A));
        }

        private MapTextureSample GetPartTextureSample(SceneObjectPart part)
        {
            return GetPartTextureSample(part, false);
        }

        private MapTextureSample GetPartTextureSample(SceneObjectPart part, bool prioritySample)
        {
            MapTextureSample fallback = new MapTextureSample
            {
                valid = false,
                color = Color.Gray,
                alpha = 1f
            };

            Primitive.TextureEntry textureEntry = part.Shape.Textures;
            if (textureEntry == null)
                return fallback;

            MapTextureSample selected = GetTextureFaceSample(textureEntry.DefaultTexture, prioritySample);

            if (textureEntry.FaceTextures != null)
            {
                foreach (Primitive.TextureEntryFace face in textureEntry.FaceTextures)
                {
                    if (face == null)
                        continue;

                    MapTextureSample faceSample = GetTextureFaceSample(face, prioritySample);
                    if (!faceSample.valid)
                        continue;

                    if (!selected.valid || faceSample.alpha < selected.alpha)
                        selected = faceSample;
                }
            }

            return selected.valid ? selected : fallback;
        }

        private MapTextureSample GetTextureFaceSample(Primitive.TextureEntryFace face)
        {
            return GetTextureFaceSample(face, false);
        }

        private MapTextureSample GetTextureFaceSample(Primitive.TextureEntryFace face, bool prioritySample)
        {
            MapTextureSample fallback = new MapTextureSample
            {
                valid = false,
                color = Color.Gray,
                alpha = 1f
            };

            if (face == null)
                return fallback;

            float faceAlpha = Math.Max(0f, Math.Min(1f, face.RGBA.A));
            Color faceColor = Color.FromArgb(
                ClampByte((int)(face.RGBA.R * 255f)),
                ClampByte((int)(face.RGBA.G * 255f)),
                ClampByte((int)(face.RGBA.B * 255f)));

            if (face.TextureID.IsZero())
            {
                fallback.valid = true;
                fallback.color = faceColor;
                fallback.alpha = faceAlpha;
                return fallback;
            }

            MapTextureSample textureSample = GetTextureAssetSample(face.TextureID, prioritySample);
            if (!textureSample.valid)
            {
                fallback.valid = true;
                fallback.color = faceColor;
                fallback.alpha = faceAlpha;
                return fallback;
            }

            textureSample.color = Multiply(textureSample.color, faceColor);
            textureSample.alpha = Math.Max(0f, Math.Min(1f, textureSample.alpha * faceAlpha));
            return textureSample;
        }

        private MapTextureSample GetTextureAssetSample(UUID textureID)
        {
            return GetTextureAssetSample(textureID, false);
        }

        private MapTextureSample GetTextureAssetSample(UUID textureID, bool prioritySample)
        {
            MapTextureSample sample;
            if (m_textureSampleCache.TryGetValue(textureID, out sample))
                return sample;

            sample = new MapTextureSample
            {
                valid = false,
                color = Color.Gray,
                alpha = 1f
            };

            if (!prioritySample &&
                m_maxTextureAssetSamplesThisPass > 0 &&
                m_textureAssetSamplesThisPass >= m_maxTextureAssetSamplesThisPass)
                return sample;

            m_textureAssetSamplesThisPass++;
            byte[] textureData = GetAssetDataForMap(textureID.ToString());
            if (textureData == null || textureData.Length == 0)
            {
                m_textureSampleCache[textureID] = sample;
                return sample;
            }

            try
            {
                Image image = DecodeMapImage(textureData);
                if (image != null)
                {
                    using (image)
                    using (Bitmap bitmap = new Bitmap(image))
                    {
                        sample = ComputeTextureSample(bitmap);
                    }
                }
            }
            catch (Exception)
            {
                sample.valid = false;
            }

            m_textureSampleCache[textureID] = sample;
            return sample;
        }

        private static MapTextureSample ComputeTextureSample(Bitmap bitmap)
        {
            long r = 0;
            long g = 0;
            long b = 0;
            long a = 0;
            long detailR = 0;
            long detailG = 0;
            long detailB = 0;
            int pixels = Math.Max(1, bitmap.Width * bitmap.Height);
            int detailPixels = 0;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Color color = bitmap.GetPixel(x, y);
                    r += color.R;
                    g += color.G;
                    b += color.B;
                    a += color.A;
                    float gray = (color.R * 0.299f) + (color.G * 0.587f) + (color.B * 0.114f);
                    if (gray > 28f && gray < 236f && color.A > 24)
                    {
                        detailR += color.R;
                        detailG += color.G;
                        detailB += color.B;
                        detailPixels++;
                    }
                }
            }

            if (detailPixels > Math.Max(8, pixels / 20))
            {
                r = (long)((r * 0.35f) + ((detailR / detailPixels) * pixels * 0.65f));
                g = (long)((g * 0.35f) + ((detailG / detailPixels) * pixels * 0.65f));
                b = (long)((b * 0.35f) + ((detailB / detailPixels) * pixels * 0.65f));
            }

            return new MapTextureSample
            {
                valid = true,
                color = Color.FromArgb(
                    ClampByte((int)(r / pixels)),
                    ClampByte((int)(g / pixels)),
                    ClampByte((int)(b / pixels))),
                alpha = Math.Max(0f, Math.Min(1f, (float)a / (255f * pixels)))
            };
        }

        private static int ApplyTextureAlpha(int opacity, float textureAlpha, int minimumOpacity)
        {
            if (textureAlpha >= 0.99f)
                return opacity;

            return Math.Max(minimumOpacity, ClampByte((int)(opacity * textureAlpha)));
        }

        private static Color ClampBrightness(Color color, int minimumBrightness, int maximumBrightness)
        {
            int max = Math.Max(color.R, Math.Max(color.G, color.B));
            int min = Math.Min(color.R, Math.Min(color.G, color.B));
            int brightness = (max + min) / 2;

            if (brightness < minimumBrightness)
                return Blend(color, Color.White, (minimumBrightness - brightness) / 255f);

            if (brightness > maximumBrightness)
                return Blend(color, Color.Black, (brightness - maximumBrightness) / 255f);

            return color;
        }

        private static Color Darken(Color color, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                ClampByte((int)(color.R * amount)),
                ClampByte((int)(color.G * amount)),
                ClampByte((int)(color.B * amount)));
        }

        private static Color ShadeFaceColor(Color color, Vector3 a, Vector3 b, Vector3 c)
        {
            float abx = b.X - a.X;
            float aby = b.Y - a.Y;
            float abz = b.Z - a.Z;
            float acx = c.X - a.X;
            float acy = c.Y - a.Y;
            float acz = c.Z - a.Z;

            float nx = (aby * acz) - (abz * acy);
            float ny = (abz * acx) - (abx * acz);
            float nz = (abx * acy) - (aby * acx);
            float length = (float)Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));

            if (length <= 0.0001f)
                return color;

            nx /= length;
            ny /= length;
            nz /= length;

            float dot = Math.Abs((nx * -0.35f) + (ny * -0.45f) + (nz * 0.82f));
            float shade = 0.78f + (dot * 0.34f);
            return Color.FromArgb(
                ClampByte((int)(color.R * shade)),
                ClampByte((int)(color.G * shade)),
                ClampByte((int)(color.B * shade)));
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                ClampByte((int)(from.R + ((to.R - from.R) * amount))),
                ClampByte((int)(from.G + ((to.G - from.G) * amount))),
                ClampByte((int)(from.B + ((to.B - from.B) * amount))));
        }

        private static Color Multiply(Color first, Color second)
        {
            return Color.FromArgb(
                ClampByte((first.R * second.R) / 255),
                ClampByte((first.G * second.G) / 255),
                ClampByte((first.B * second.B) / 255));
        }

        private static int ClampByte(int value)
        {
            if (value < 0)
                return 0;

            if (value > 255)
                return 255;

            return value;
        }

        private Point project(ITerrainChannel hm, Vector3 point3d, Vector3 originpos)
        {
            Point returnpt = new Point();
            //originpos = point3d;
            //int d = (int)(256f / 1.5f);

            //Vector3 topos = new Vector3(0, 0, 0);
            // float z = -point3d.z - topos.z;

            returnpt.X = (int)point3d.X;//(int)((topos.x - point3d.x) / z * d);
            returnpt.Y = (int)((hm.Width - 1) - point3d.Y);//(int)(255 - (((topos.y - point3d.y) / z * d)));

            return returnpt;
        }

        public Bitmap CreateViewImage(Vector3 camPos, Vector3 camDir, float fov, int width, int height, bool useTextures)
        {
            return null;
        }
    }
}
