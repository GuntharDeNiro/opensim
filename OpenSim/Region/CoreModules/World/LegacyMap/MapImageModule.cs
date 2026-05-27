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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
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
        public Pen outlinePen;
        public face[] trns;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MapImageModule")]
    public class MapImageModule : IMapImageGenerator, INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private IConfigSource m_config;
        private IMapTileTerrainRenderer terrainRenderer;
        private bool m_Enabled = false;
        private static readonly string[] MapConfigSections = new string[] { "Map", "Startup" };

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

            ManagedImage managedImage;
            Image image;

            try
            {
                if (OpenJPEG.DecodeToImage(asset.Data, out managedImage, out image))
                    return new Bitmap(image);
                else
                    return null;
            }
            catch (DllNotFoundException)
            {
                m_log.ErrorFormat("[MAPTILE]: OpenJpeg is not installed correctly on this system.   Asset Data is empty for {0}", id);

            }
            catch (IndexOutOfRangeException)
            {
                m_log.ErrorFormat("[MAPTILE]: OpenJpeg was unable to decode this.   Asset Data is empty for {0}", id);

            }
            catch (Exception)
            {
                m_log.ErrorFormat("[MAPTILE]: OpenJpeg was unable to decode this.   Asset Data is empty for {0}", id);

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
            bool prettyObjectVolume = Util.GetConfigVarFromSections<bool>(
                m_config, "PrettyPrimVolumeOnMapTile", MapConfigSections, true);
            bool drawObjectOutlines = Util.GetConfigVarFromSections<bool>(
                m_config, "MapObjectVolumeOutlines", MapConfigSections, true);
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
            int waterObjectOpacity = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapWaterObjectVolumeOpacity", MapConfigSections, 38));
            int minimumBrightness = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMinimumBrightness", MapConfigSections, 72));
            int maximumBrightness = ClampByte(Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeMaximumBrightness", MapConfigSections, 235));
            int largeObjectArea = Math.Max(1, Util.GetConfigVarFromSections<int>(
                m_config, "MapObjectVolumeLargeArea", MapConfigSections, 1800));

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
                                if (part == null)
                                    continue;

                                // Draw if the object is at least 1 meter wide in any direction
                                if (part.Scale.X > 1f || part.Scale.Y > 1f || part.Scale.Z > 1f)
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

                                        if (part.Shape.PCode == (byte)PCode.Tree || part.Shape.PCode == (byte)PCode.NewTree || part.Shape.PCode == (byte)PCode.Grass)
                                            continue; // eliminates trees from this since we don't really have a good tree representation
                                        // if you want tree blocks on the map comment the above line and uncomment the below line
                                        //mapdotspot = Color.PaleGreen;

                                        mapdotspot = GetPartMapColor(part, mapdotspot, prettyObjectVolume,
                                            minimumBrightness, maximumBrightness);
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
                                        bool isWaterLikeObject = prettyObjectVolume &&
                                            IsWaterLikeMapObject(mapdotspot, objectArea, part.Scale, largeObjectArea);

                                        if (prettyObjectVolume && useTextureAlpha)
                                            fillOpacity = ApplyTextureAlpha(fillOpacity, GetPartTextureAlpha(part), minimumOpacity);

                                        if (isWaterLikeObject)
                                            fillOpacity = Math.Min(fillOpacity, waterObjectOpacity);

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
                                        ds.outlinePen = null;
                                        if (prettyObjectVolume && drawObjectOutlines && !isWaterLikeObject)
                                            ds.outlinePen = new Pen(Color.FromArgb(outlineOpacity, Darken(mapdotspot, 0.55f)), 1f);
                                        //ds.rect = new Rectangle(mapdrawstartX, (255 - mapdrawstartY), mapdrawendX - mapdrawstartX, mapdrawendY - mapdrawstartY);

                                        ds.trns = new face[FaceA.Length];

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
                            if (z_sort.ContainsKey(sortedlocalIds[s]))
                            {
                                DrawStruct rectDrawStruct = z_sort[sortedlocalIds[s]];
                                for (int r = 0; r < rectDrawStruct.trns.Length; r++)
                                {
                                    g.FillPolygon(rectDrawStruct.brush,rectDrawStruct.trns[r].pts);
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
                    if (ds.outlinePen != null)
                        ds.outlinePen.Dispose();
                }
            }

            m_log.Debug("[MAPTILE]: Generating Maptile Step 2: Done in " + (Environment.TickCount - tc) + " ms");

            return mapbmp;
        }

        private Color GetPartMapColor(SceneObjectPart part, Color fallback, bool prettyObjectVolume,
            int minimumBrightness, int maximumBrightness)
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
            adjusted = ClampBrightness(adjusted, minimumBrightness, maximumBrightness);

            if (adjusted.R > 246 && adjusted.G > 246 && adjusted.B > 246)
                adjusted = Color.FromArgb(224, 224, 216);

            return adjusted;
        }

        private float GetPartTextureAlpha(SceneObjectPart part)
        {
            Primitive.TextureEntry textureEntry = part.Shape.Textures;

            if (textureEntry == null || textureEntry.DefaultTexture == null)
                return 1f;

            return Math.Max(0f, Math.Min(1f, textureEntry.DefaultTexture.RGBA.A));
        }

        private static int ApplyTextureAlpha(int opacity, float textureAlpha, int minimumOpacity)
        {
            if (textureAlpha >= 0.99f)
                return opacity;

            return Math.Max(minimumOpacity, ClampByte((int)(opacity * textureAlpha)));
        }

        private static bool IsWaterLikeMapObject(Color color, int objectArea, Vector3 scale, int largeObjectArea)
        {
            if (objectArea < largeObjectArea)
                return false;

            float largestSide = Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
            float thinnestSide = Math.Min(scale.X, Math.Min(scale.Y, scale.Z));

            if (largestSide < 24f || thinnestSide > 3f)
                return false;

            int max = Math.Max(color.R, Math.Max(color.G, color.B));
            int min = Math.Min(color.R, Math.Min(color.G, color.B));
            int brightness = (max + min) / 2;
            bool paleOverlay = brightness > 145 && (max - min) < 70;
            bool blueOrCyan = color.B >= color.R - 8 && color.G >= color.R - 35;

            return paleOverlay && blueOrCyan;
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

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                ClampByte((int)(from.R + ((to.R - from.R) * amount))),
                ClampByte((int)(from.G + ((to.G - from.G) * amount))),
                ClampByte((int)(from.B + ((to.B - from.B) * amount))));
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
