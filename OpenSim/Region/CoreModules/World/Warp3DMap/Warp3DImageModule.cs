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
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime;

using CSJ2K;
using Nini.Config;
using log4net;
using Warp3D;
using Mono.Addins;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.Imaging;
using OpenMetaverse.Rendering;
using OpenMetaverse.StructuredData;

using WarpRenderer = Warp3D.Warp3D;

namespace OpenSim.Region.CoreModules.World.Warp3DMap
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "Warp3DImageModule")]
    public class Warp3DImageModule : IMapImageGenerator, INonSharedRegionModule
    {
        private static readonly Color4 WATER_COLOR = new Color4(29, 72, 96, 216);
//        private static readonly Color4 WATER_COLOR = new Color4(29, 72, 96, 128);

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

#pragma warning disable 414
        private static string LogHeader = "[WARP 3D IMAGE MODULE]";
#pragma warning restore 414

        internal Scene m_scene;
        private IRendering m_primMesher;
        internal IJ2KDecoder m_imgDecoder;

        // caches per rendering 
        private Dictionary<UUID, warp_Texture> m_warpTextures;
        private Dictionary<UUID, int> m_colors;
        private Dictionary<UUID, MapSpriteTexture> m_spriteTextures;

        private bool m_drawPrimVolume = true;   // true if should render the prims on the tile
        private bool m_textureTerrain = true;   // true if to create terrain splatting texture
        private bool m_textureAverageTerrain = false; // replace terrain textures by their average color
        private bool m_texturePrims = true;     // true if should texture the rendered prims
        private float m_texturePrimSize = 48f;  // size of prim before we consider texturing it
        private bool m_renderMeshes = true;     // true if to render meshes rather than just bounding boxes
        private bool m_useCachedAssetsOnly = true;
        private bool m_skipMissingExternalGeometry = true;
        private bool m_skipFlatTextureCardsWithoutTexture = true;
        private bool m_forceGC = false;
        private int m_renderTimeBudgetMS = 30000;
        private int m_maxMeshAssetDecodes = 2048;
        private int m_maxTextureAssetDecodes = 192;
        private int m_textureDownsample = 8;
        private bool m_drawFlatTextureCardSprites = true;
        private int m_spriteRenderTimeBudgetMS = 12000;
        private int m_maxSpriteTextureDecodes = 512;
        private int m_spriteTextureMaxSize = 256;
        private float m_spriteMinAlphaCoverage = 0.02f;
        private float m_spriteMaxOpaqueCoverage = 0.98f;
        private float m_spriteMaxSizeMeters = 32f;
        private bool m_waterDepthShading = true;
        private Color m_waterBaseColor = Color.FromArgb(29, 72, 96);
        private Color m_waterShallowColor = Color.FromArgb(108, 135, 145);
        private Color m_waterDeepColor = Color.FromArgb(36, 75, 90);
        private float m_waterDepthRange = 36f;
        private float m_waterNoiseStrength = 0.025f;
        private float m_waterDepthOpacity = 0.85f;

        private const float m_cameraHeight = 4096f;
        private float m_renderMinHeight = -100f;
        private float m_renderMaxHeight = 4096f;

        private bool m_Enabled = false;
        private readonly HashSet<string> m_failedGeometryAssets = new HashSet<string>();
        private readonly HashSet<UUID> m_failedTextureAssets = new HashSet<UUID>();
        private int m_renderStartMS;
        private int m_renderedParts;
        private int m_renderedFaces;
        private int m_missingGeometrySkipped;
        private int m_budgetSkipped;
        private int m_flatTextureCardSkipped;
        private int m_meshAssetDecodesThisPass;
        private int m_textureAssetDecodesThisPass;
        private int m_spriteStartMS;
        private int m_spriteCardsDrawn;
        private int m_spriteCardsSkipped;
        private int m_spriteTextureDecodesThisPass;
        private int m_waterDepthPixels;

        private sealed class MapSpriteTexture : IDisposable
        {
            public Bitmap Bitmap;
            public float AlphaCoverage;
            public float OpaqueCoverage;

            public void Dispose()
            {
                Bitmap?.Dispose();
                Bitmap = null;
            }
        }

        #region Region Module interface

        public void Initialise(IConfigSource source)
        {
            string[] configSections = new string[] { "Map", "Startup" };

            if (Util.GetConfigVarFromSections<string>(
                source, "MapImageModule", configSections, "MapImageModule") != "Warp3DImageModule")
                return;

            m_Enabled = true;

            m_drawPrimVolume =
                Util.GetConfigVarFromSections<bool>(source, "DrawPrimOnMapTile", configSections, m_drawPrimVolume);
            m_textureTerrain =
                Util.GetConfigVarFromSections<bool>(source, "TextureOnMapTile", configSections, m_textureTerrain);
            m_textureAverageTerrain =
                Util.GetConfigVarFromSections<bool>(source, "AverageTextureColorOnMapTile", configSections, m_textureAverageTerrain);
            if (m_textureAverageTerrain)
                m_textureTerrain = true;
            m_texturePrims =
                Util.GetConfigVarFromSections<bool>(source, "TexturePrims", configSections, m_texturePrims);
            m_texturePrimSize =
                Util.GetConfigVarFromSections<float>(source, "TexturePrimSize", configSections, m_texturePrimSize);
            m_renderMeshes =
                Util.GetConfigVarFromSections<bool>(source, "RenderMeshes", configSections, m_renderMeshes);
            m_useCachedAssetsOnly =
                Util.GetConfigVarFromSections<bool>(source, "Map3DUseCachedAssetsOnly", configSections, m_useCachedAssetsOnly);
            m_skipMissingExternalGeometry =
                Util.GetConfigVarFromSections<bool>(source, "Map3DSkipMissingExternalGeometry", configSections, m_skipMissingExternalGeometry);
            m_skipFlatTextureCardsWithoutTexture =
                Util.GetConfigVarFromSections<bool>(source, "Map3DSkipFlatTextureCardsWithoutTexture", configSections, m_skipFlatTextureCardsWithoutTexture);
            m_forceGC =
                Util.GetConfigVarFromSections<bool>(source, "Map3DForceGC", configSections, m_forceGC);
            m_renderTimeBudgetMS = Math.Max(0,
                Util.GetConfigVarFromSections<int>(source, "Map3DRenderTimeBudgetMS", configSections, m_renderTimeBudgetMS));
            m_maxMeshAssetDecodes = Math.Max(0,
                Util.GetConfigVarFromSections<int>(source, "Map3DMaxMeshAssetDecodes", configSections, m_maxMeshAssetDecodes));
            m_maxTextureAssetDecodes = Math.Max(0,
                Util.GetConfigVarFromSections<int>(source, "Map3DMaxTextureAssetDecodes", configSections, m_maxTextureAssetDecodes));
            m_textureDownsample = Math.Max(1, Math.Min(10,
                Util.GetConfigVarFromSections<int>(source, "Map3DTextureDownsample", configSections, m_textureDownsample)));
            m_drawFlatTextureCardSprites =
                Util.GetConfigVarFromSections<bool>(source, "Map3DDrawFlatTextureCardSprites", configSections, m_drawFlatTextureCardSprites);
            m_spriteRenderTimeBudgetMS = Math.Max(0,
                Util.GetConfigVarFromSections<int>(source, "Map3DTextureCardSpriteBudgetMS", configSections, m_spriteRenderTimeBudgetMS));
            m_maxSpriteTextureDecodes = Math.Max(0,
                Util.GetConfigVarFromSections<int>(source, "Map3DMaxSpriteTextureDecodes", configSections, m_maxSpriteTextureDecodes));
            m_spriteTextureMaxSize = Math.Max(32, Math.Min(1024,
                Util.GetConfigVarFromSections<int>(source, "Map3DSpriteTextureMaxSize", configSections, m_spriteTextureMaxSize)));
            m_spriteMinAlphaCoverage = Math.Max(0f, Math.Min(1f,
                Util.GetConfigVarFromSections<float>(source, "Map3DSpriteMinAlphaCoverage", configSections, m_spriteMinAlphaCoverage)));
            m_spriteMaxOpaqueCoverage = Math.Max(0f, Math.Min(1f,
                Util.GetConfigVarFromSections<float>(source, "Map3DSpriteMaxOpaqueCoverage", configSections, m_spriteMaxOpaqueCoverage)));
            m_spriteMaxSizeMeters = Math.Max(1f,
                Util.GetConfigVarFromSections<float>(source, "Map3DSpriteMaxSizeMeters", configSections, m_spriteMaxSizeMeters));
            m_waterBaseColor = ColorTranslator.FromHtml(Util.GetConfigVarFromSections<string>(
                source, "MapColorWater", configSections, "#1D475F"));
            m_waterDepthShading = Util.GetConfigVarFromSections<bool>(
                source, "MapWaterDepthShading", configSections, m_waterDepthShading);
            m_waterDepthShading = Util.GetConfigVarFromSections<bool>(
                source, "Map3DWaterDepthShading", configSections, m_waterDepthShading);
            m_waterShallowColor = ColorTranslator.FromHtml(Util.GetConfigVarFromSections<string>(
                source, "MapWaterShallowColor", configSections, "#6C8791"));
            m_waterDeepColor = ColorTranslator.FromHtml(Util.GetConfigVarFromSections<string>(
                source, "MapWaterDeepColor", configSections, "#244B5A"));
            m_waterDepthRange = Math.Max(1f, Util.GetConfigVarFromSections<float>(
                source, "MapWaterDepthRange", configSections, m_waterDepthRange));
            m_waterNoiseStrength = Math.Max(0f, Math.Min(0.2f, Util.GetConfigVarFromSections<float>(
                source, "MapWaterNoiseStrength", configSections, m_waterNoiseStrength)));
            m_waterDepthOpacity = Math.Max(0f, Math.Min(1f, Util.GetConfigVarFromSections<float>(
                source, "Map3DWaterDepthOpacity", configSections, m_waterDepthOpacity)));

            m_renderMaxHeight = Util.GetConfigVarFromSections<float>(source, "RenderMaxHeight", configSections, m_renderMaxHeight);
            m_renderMinHeight = Util.GetConfigVarFromSections<float>(source, "RenderMinHeight", configSections, m_renderMinHeight);
            /*
            m_cameraHeight = Util.GetConfigVarFromSections<float>(m_config, "RenderCameraHeight", configSections, m_cameraHeight);

            if (m_cameraHeight < 250f)
                m_cameraHeight = 250f;
            else if (m_cameraHeight > 4096f)
                m_cameraHeight = 4096f;
            */
            if (m_renderMaxHeight < 100f)
                m_renderMaxHeight = 100f;
            else if (m_renderMaxHeight > m_cameraHeight - 10f)
                m_renderMaxHeight = m_cameraHeight - 10f;

            if (m_renderMinHeight < -100f)
                m_renderMinHeight = -100f;
            else if (m_renderMinHeight > m_renderMaxHeight - 10f)
                m_renderMinHeight = m_renderMaxHeight - 10f;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;

            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
                m_log.Info("[MAPTILE]: Loaded prim mesher " + renderers[0]);
            else
                m_log.Info("[MAPTILE]: No prim mesher loaded, prim rendering will be disabled");

            m_scene.RegisterModuleInterface<IMapImageGenerator>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_imgDecoder = m_scene.RequestModuleInterface<IJ2KDecoder>();
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "Warp3DImageModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        #region IMapImageGenerator Members

        private Vector3 cameraPos;
        private Vector3 cameraDir;
        private int viewWidth = 256;
        private int viewHeight = 256;
        private float fov;
        private bool orto;

        public Bitmap CreateMapTile()
        {
            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
            {
                m_primMesher = RenderingLoader.LoadRenderer(renderers[0]);
            }

            try
            {
                viewWidth = (int)m_scene.RegionInfo.RegionSizeX;
                viewHeight = (int)m_scene.RegionInfo.RegionSizeY;

                cameraPos = new Vector3(
                                viewWidth * 0.5f,
                                viewHeight * 0.5f,
                                m_cameraHeight);

                cameraDir = -Vector3.UnitZ;
                orto = true;

                Bitmap tile = GenImage();
                // image may be reloaded elsewhere, so no compression format
                string filename = "MAP-" + m_scene.RegionInfo.RegionID.ToString() + ".png";
                tile.Save(filename,ImageFormat.Png);
                return tile;
            }
            finally
            {
                m_primMesher = null;
            }
        }

        public Bitmap CreateViewImage(Vector3 camPos, Vector3 camDir, float pfov, int width, int height, bool useTextures)
        {
            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
            {
                m_primMesher = RenderingLoader.LoadRenderer(renderers[0]);
            }

            cameraPos = camPos;
            cameraDir = camDir;
            viewWidth = width;
            viewHeight = height;
            fov = pfov;
            orto = false;

            try
            {
                return GenImage();
            }
            finally
            {
                m_primMesher = null;
            }
        }

        private Bitmap GenImage()
        {
            m_colors= new Dictionary<UUID, int>();
            m_warpTextures= new Dictionary<UUID, warp_Texture>();
            m_spriteTextures = new Dictionary<UUID, MapSpriteTexture>();
            m_failedGeometryAssets.Clear();
            m_failedTextureAssets.Clear();
            m_renderStartMS = Environment.TickCount;
            m_renderedParts = 0;
            m_renderedFaces = 0;
            m_missingGeometrySkipped = 0;
            m_budgetSkipped = 0;
            m_flatTextureCardSkipped = 0;
            m_meshAssetDecodesThisPass = 0;
            m_textureAssetDecodesThisPass = 0;
            m_spriteCardsDrawn = 0;
            m_spriteCardsSkipped = 0;
            m_spriteTextureDecodesThisPass = 0;
            m_waterDepthPixels = 0;

            WarpRenderer renderer = new WarpRenderer();

            if (!renderer.CreateScene(viewWidth, viewHeight))
                return new Bitmap(viewWidth, viewHeight);

            #region Camera

            warp_Vector pos = ConvertVector(ref cameraPos);
            warp_Vector lookat = ConvertVector(cameraPos + cameraDir);

            if (orto)
                renderer.Scene.defaultCamera.setOrthographic(true, viewWidth, viewHeight);
            else
                renderer.Scene.defaultCamera.setFov(fov);

            renderer.Scene.defaultCamera.setPos(pos);
            renderer.Scene.defaultCamera.lookAt(lookat);
            #endregion Camera

            renderer.Scene.setAmbient(warp_Color.getColor(192, 191, 173));
            renderer.Scene.addLight("Light1", new warp_Light(new warp_Vector(0f, 1f, 8f), warp_Color.White, 0, 200, 20));

            CreateWater(renderer);
            CreateTerrain(renderer);
            if (m_drawPrimVolume)
                CreateAllPrims(renderer);

            renderer.Render();

            Bitmap bitmap = renderer.Scene.getImage();
            ApplyWaterDepthShading(bitmap);
            if (m_drawFlatTextureCardSprites)
                DrawFlatTextureCardSprites(bitmap);

            renderer.Scene.destroy();
            renderer.Reset();
            renderer = null;

            DisposeSpriteTextures();
            m_colors = null;
            m_warpTextures = null;
            m_spriteTextures = null;

            if (m_forceGC)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
            }

            m_log.DebugFormat(
                "[WARP 3D IMAGE MODULE]: Rendered {0} parts/{1} faces, drew {2} texture-card sprites, depth-shaded {3} water pixels, skipped {4} missing exact geometry, {5} flat texture-card faces, {6} texture-card sprites and {7} budget-limited items in {8}ms",
                m_renderedParts, m_renderedFaces, m_spriteCardsDrawn, m_waterDepthPixels,
                m_missingGeometrySkipped, m_flatTextureCardSkipped, m_spriteCardsSkipped, m_budgetSkipped,
                Util.EnvironmentTickCountSubtract(Environment.TickCount, m_renderStartMS));
            return bitmap;
        }

        public byte[] WriteJpeg2000Image()
        {
            try
            {
                using (Bitmap mapbmp = CreateMapTile())
                    return OpenJPEG.EncodeFromImage(mapbmp, false);
            }
            catch (Exception e)
            {
                // JPEG2000 encoder failed
                m_log.Error("[WARP 3D IMAGE MODULE]: Failed generating terrain map: ", e);
            }

            return null;
        }

        #endregion

        #region Rendering Methods

        // Add a water plane to the renderer.
        private void CreateWater(WarpRenderer renderer)
        {
            float waterHeight = (float)m_scene.RegionInfo.RegionSettings.WaterHeight;

            renderer.AddPlane("Water", m_scene.RegionInfo.RegionSizeX * 0.5f, false);
            renderer.Scene.sceneobject("Water").setPos(m_scene.RegionInfo.RegionSizeX * 0.5f,
                                                       waterHeight,
                                                       m_scene.RegionInfo.RegionSizeY * 0.5f);

            warp_Material waterMaterial = new warp_Material(ConvertColor(WATER_COLOR));
            renderer.Scene.addMaterial("WaterMat", waterMaterial);
            renderer.SetObjectMaterial("Water", "WaterMat");
        }

        private void ApplyWaterDepthShading(Bitmap bitmap)
        {
            if (!m_waterDepthShading || bitmap == null || m_scene?.Heightmap == null)
                return;

            ITerrainChannel terrain = m_scene.Heightmap;
            if (terrain.Width <= 0 || terrain.Height <= 0)
                return;

            float waterHeight = (float)m_scene.RegionInfo.RegionSettings.WaterHeight;
            float scaleX = (terrain.Width - 1f) / Math.Max(1, bitmap.Width - 1);
            float scaleY = (terrain.Height - 1f) / Math.Max(1, bitmap.Height - 1);

            for (int py = 0; py < bitmap.Height; py++)
            {
                int ty = ClampInt((int)Math.Round((bitmap.Height - 1 - py) * scaleY), 0, terrain.Height - 1);

                for (int px = 0; px < bitmap.Width; px++)
                {
                    int tx = ClampInt((int)Math.Round(px * scaleX), 0, terrain.Width - 1);
                    float depth = waterHeight - terrain[tx, ty];
                    if (depth <= 0f)
                        continue;

                    Color pixel = bitmap.GetPixel(px, py);
                    float blendAmount = GetWaterPixelBlendAmount(pixel);
                    if (blendAmount <= 0f)
                        continue;

                    Color waterColor = GetWaterDepthColor(depth, tx, ty);
                    bitmap.SetPixel(px, py, Blend(pixel, waterColor, blendAmount));
                    m_waterDepthPixels++;
                }
            }
        }

        private Color GetWaterDepthColor(float depth, int x, int y)
        {
            float ratio = Math.Max(0f, Math.Min(1f, depth / m_waterDepthRange));
            ratio = SmoothStep(ratio);

            float noise = (float)TerrainUtil.InterpolatedNoise(x / 11.0, y / 11.0) * m_waterNoiseStrength;
            float fineNoise = (float)TerrainUtil.InterpolatedNoise(x + 91, y + 17) * m_waterNoiseStrength * 0.45f;
            ratio = Math.Max(0f, Math.Min(1f, ratio + noise + fineNoise));

            Color depthColor = Blend(m_waterShallowColor, m_waterDeepColor, ratio);
            return Blend(depthColor, m_waterBaseColor, 0.22f);
        }

        private float GetWaterPixelBlendAmount(Color pixel)
        {
            if (pixel.A == 0)
                return 0f;

            int max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            int min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            if (max < 18)
                return 0f;

            // Keep labels, bright boats and warm/green object pixels from being tinted as water.
            if (pixel.R > 225 && pixel.G > 225 && pixel.B > 225)
                return 0f;
            if (pixel.R > pixel.G + 45 && pixel.R > pixel.B + 45)
                return 0f;
            if (pixel.G > pixel.R + 55 && pixel.G > pixel.B + 35)
                return 0f;

            int baseDistance =
                ((pixel.R - m_waterBaseColor.R) * (pixel.R - m_waterBaseColor.R)) +
                ((pixel.G - m_waterBaseColor.G) * (pixel.G - m_waterBaseColor.G)) +
                ((pixel.B - m_waterBaseColor.B) * (pixel.B - m_waterBaseColor.B));

            bool blueGreenWater = pixel.B >= pixel.R + 8 && pixel.G >= pixel.R + 2;
            bool mutedWater = (max - min) < 48 && pixel.B >= pixel.R - 4 && pixel.G >= pixel.R - 8;
            bool nearBaseWater = baseDistance < 18000;

            if (!blueGreenWater && !mutedWater && !nearBaseWater)
                return 0f;

            float confidence = blueGreenWater ? 1f : 0.65f;
            if (nearBaseWater)
                confidence = Math.Max(confidence, 0.8f);

            return m_waterDepthOpacity * confidence;
        }

        // Add a terrain to the renderer.
        // Note that we create a 'low resolution' 257x257 vertex terrain rather than trying for
        //    full resolution. This saves a lot of memory especially for very large regions.
        private void CreateTerrain(WarpRenderer renderer)
        {
            ITerrainChannel terrain = m_scene.Heightmap;

            float regionsx = m_scene.RegionInfo.RegionSizeX;
            float regionsy = m_scene.RegionInfo.RegionSizeY;

            // 'diff' is the difference in scale between the real region size and the size of terrain we're buiding

            int bitWidth = Util.intLog2((uint)terrain.Width);
            int bitHeight = Util.intLog2((uint)terrain.Height);
            if(bitHeight > bitWidth)
                bitWidth = bitHeight;

            if (bitWidth > 8) // more than 256 is very heavy :(
                bitWidth = 8;

            int twidth = 1 << bitWidth;

            float diff = regionsx / twidth;

            int npointsx = (int)(regionsx / diff);
            int npointsy = (int)(regionsy / diff);

            float invsx = 1.0f / (npointsx * diff);
            float invsy = 1.0f / (npointsy * diff);

            npointsx++;
            npointsy++;

            // Create all the vertices for the terrain
            warp_Object obj = new warp_Object();
            float x, y;
            float tv;
            for (y = 0; y < regionsy; y += diff)
            {
                tv = y * invsy;
                for (x = 0; x < regionsx; x += diff)
                    obj.addVertex(x, terrain[(int)x, (int)y], y, x * invsx, tv);
                obj.addVertex(x, terrain[(int)(x - diff), (int)y], y, 1.0f, tv);
            }

            int lastY = (int)(y - diff);
            for (x = 0; x < regionsx; x += diff)
                obj.addVertex(x, terrain[(int)x, lastY], y, x * invsx, 1.0f);
            obj.addVertex(x, terrain[(int)(x - diff), lastY],y, 1.0f, 1.0f);

            // create triangles.
            int limx = npointsx - 1;
            int limy = npointsy - 1;
            for (int j = 0; j < limy; j++)
            {
                for (int i = 0; i < limx; i++)
                {
                    int v = j * npointsx + i;

                    // Make two triangles for each of the squares in the grid of vertices
                    obj.addTriangle(v, v + 1, v + npointsx);
                    obj.addTriangle( v + npointsx + 1, v + npointsx, v + 1);
                }
            }

            renderer.Scene.addObject("Terrain", obj);

            OpenSim.Framework.RegionSettings regionInfo = m_scene.RegionInfo.RegionSettings;
            UUID[] textureIDs = new UUID[4]
            {
                regionInfo.TerrainTexture1,
                regionInfo.TerrainTexture2,
                regionInfo.TerrainTexture3,
                regionInfo.TerrainTexture4,
            };

            float[] startHeights = new float[4]
            {
                (float)regionInfo.Elevation1SW,
                (float)regionInfo.Elevation1NW,
                (float)regionInfo.Elevation1SE,
                (float)regionInfo.Elevation1NE
            };

            float[] heightRanges = new float[4]
            {
                (float)regionInfo.Elevation2SW,
                (float)regionInfo.Elevation2NW,
                (float)regionInfo.Elevation2SE,
                (float)regionInfo.Elevation2NE
            };

            warp_Texture texture;
            using (Bitmap image = TerrainSplat.Splat(terrain, textureIDs, startHeights, heightRanges,
                        m_scene.RegionInfo.WorldLocX, m_scene.RegionInfo.WorldLocY,
                        m_scene.AssetService, m_imgDecoder, m_textureTerrain, m_textureAverageTerrain,
                        twidth, twidth))
                    texture = new warp_Texture(image);

            warp_Material material = new warp_Material(texture);
            obj.setMaterial(material);
            renderer.Scene.addMaterial("TerrainMat", material);
        }

        private void CreateAllPrims(WarpRenderer renderer)
        {
            if (m_primMesher == null)
                return;

            bool budgetExhausted = false;
            m_scene.ForEachSOG(
                delegate (SceneObjectGroup group)
                {
                    if (budgetExhausted)
                        return;

                    foreach (SceneObjectPart child in group.Parts)
                    {
                        if (Map3DBudgetExpired())
                        {
                            m_budgetSkipped++;
                            budgetExhausted = true;
                            break;
                        }

                        try { CreatePrim(renderer, child); }
                        catch (Exception e)
                        {
                            m_log.Debug($"[Warp3D] failed to render prim {child.Name} at {child.GetWorldPosition()}: {e.Message}");
                        }
                    }
                }
            );
        }

        private void DrawFlatTextureCardSprites(Bitmap bitmap)
        {
            if (bitmap == null)
                return;

            m_spriteStartMS = Environment.TickCount;

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.SmoothingMode = SmoothingMode.None;

                bool budgetExhausted = false;
                m_scene.ForEachSOG(
                    delegate (SceneObjectGroup group)
                    {
                        if (budgetExhausted)
                            return;

                        foreach (SceneObjectPart child in group.Parts)
                        {
                            if (TextureCardSpriteBudgetExpired())
                            {
                                m_budgetSkipped++;
                                budgetExhausted = true;
                                break;
                            }

                            if (!IsLikelyFlatTextureCard(child))
                                continue;

                            try
                            {
                                if (!TryGetTextureCardSpriteFace(child, out Primitive.TextureEntryFace face))
                                {
                                    m_spriteCardsSkipped++;
                                    continue;
                                }

                                MapSpriteTexture sprite = GetSpriteTexture(face.TextureID, child);
                                if (sprite == null ||
                                    sprite.AlphaCoverage < m_spriteMinAlphaCoverage ||
                                    sprite.OpaqueCoverage > m_spriteMaxOpaqueCoverage)
                                {
                                    m_spriteCardsSkipped++;
                                    continue;
                                }

                                if (!TryDrawTextureCardSprite(graphics, bitmap, child, face, sprite.Bitmap))
                                {
                                    m_spriteCardsSkipped++;
                                    continue;
                                }

                                m_spriteCardsDrawn++;
                            }
                            catch (Exception e)
                            {
                                m_spriteCardsSkipped++;
                                m_log.Debug($"[Warp3D] failed to draw texture-card sprite {child.Name} at {child.GetWorldPosition()}: {e.Message}");
                            }
                        }
                    }
                );
            }
        }

        private bool TryDrawTextureCardSprite(Graphics graphics, Bitmap bitmap, SceneObjectPart part,
            Primitive.TextureEntryFace face, Bitmap texture)
        {
            if (!TryGetTextureCardSpritePlacement(part, bitmap, out float centerX, out float centerY,
                out float width, out float height, out float angleDegrees))
                return false;

            Color4 tint = face.RGBA;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix(new float[][]
                {
                    new float[] { tint.R, 0, 0, 0, 0 },
                    new float[] { 0, tint.G, 0, 0, 0 },
                    new float[] { 0, 0, tint.B, 0, 0 },
                    new float[] { 0, 0, 0, tint.A, 0 },
                    new float[] { 0, 0, 0, 0, 1 }
                });
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                System.Drawing.Drawing2D.Matrix oldTransform = graphics.Transform;
                try
                {
                    graphics.TranslateTransform(centerX, centerY);
                    graphics.RotateTransform(angleDegrees);

                    Rectangle destination = new Rectangle(
                        (int)MathF.Round(width * -0.5f),
                        (int)MathF.Round(height * -0.5f),
                        Math.Max(1, (int)MathF.Round(width)),
                        Math.Max(1, (int)MathF.Round(height)));

                    graphics.DrawImage(texture, destination, 0, 0, texture.Width, texture.Height,
                        GraphicsUnit.Pixel, attributes);
                }
                finally
                {
                    graphics.Transform = oldTransform;
                    oldTransform.Dispose();
                }
            }

            return true;
        }

        private bool TryGetTextureCardSpritePlacement(SceneObjectPart part, Bitmap bitmap,
            out float centerX, out float centerY, out float width, out float height, out float angleDegrees)
        {
            centerX = 0;
            centerY = 0;
            width = 0;
            height = 0;
            angleDegrees = 0;

            Vector3 position = part.GetWorldPosition();
            if (position.Z < m_renderMinHeight || position.Z > m_renderMaxHeight)
                return false;

            float regionWidth = m_scene.RegionInfo.RegionSizeX;
            float regionHeight = m_scene.RegionInfo.RegionSizeY;
            if (regionWidth <= 0 || regionHeight <= 0)
                return false;

            centerX = position.X * bitmap.Width / regionWidth;
            centerY = bitmap.Height - (position.Y * bitmap.Height / regionHeight);

            Vector3 scale = part.Scale;
            float widthMeters;
            float heightMeters;
            float angleOffset = 0f;

            if (scale.Z <= scale.X && scale.Z <= scale.Y)
            {
                widthMeters = scale.X;
                heightMeters = scale.Y;
            }
            else if (scale.X <= scale.Y && scale.X <= scale.Z)
            {
                widthMeters = scale.Y;
                heightMeters = scale.Z;
                angleOffset = 90f;
            }
            else
            {
                widthMeters = scale.X;
                heightMeters = scale.Z;
            }

            if (widthMeters <= 0.05f || heightMeters <= 0.05f ||
                widthMeters > m_spriteMaxSizeMeters || heightMeters > m_spriteMaxSizeMeters)
                return false;

            width = Math.Max(1f, widthMeters * bitmap.Width / regionWidth);
            height = Math.Max(1f, heightMeters * bitmap.Height / regionHeight);

            Quaternion rotation = part.GetWorldRotation();
            rotation.GetEulerAngles(out _, out _, out float yaw);
            angleDegrees = (float)(-(yaw * 180.0 / Math.PI) - angleOffset);

            return true;
        }

        private bool TryGetTextureCardSpriteFace(SceneObjectPart part, out Primitive.TextureEntryFace selectedFace)
        {
            selectedFace = null;

            Primitive.TextureEntry textures = part.Shape?.Textures;
            if (textures == null)
                return false;

            for (uint i = 0; i < 32; i++)
            {
                Primitive.TextureEntryFace face = textures.GetFace(i);
                if (IsUsableSpriteFace(face))
                {
                    selectedFace = face;
                    return true;
                }
            }

            if (IsUsableSpriteFace(textures.DefaultTexture))
            {
                selectedFace = textures.DefaultTexture;
                return true;
            }

            return false;
        }

        private static bool IsUsableSpriteFace(Primitive.TextureEntryFace face)
        {
            return face != null &&
                face.RGBA.A > 0f &&
                face.TextureID.IsNotZero() &&
                !InvPrimMagicTexture.Equals(face.TextureID);
        }

        private void UVPlanarMap(ref Vertex v, ref Vector3 scale, out float tu, out float tv)
        {
            Vector3 scaledPos = v.Position * scale;
            float d = v.Normal.X;
            if (d >= 0.5f)
            {
                tu = 2f * scaledPos.Y;
                tv = scaledPos.X * v.Normal.Z - scaledPos.Z * v.Normal.X;
            }
            else if( d <= -0.5f)
            {
                tu = -2f * scaledPos.Y;
                tv = -scaledPos.X * v.Normal.Z + scaledPos.Z * v.Normal.X;
            }
            else if (v.Normal.Y > 0f)
            {
                tu = -2f * scaledPos.X;
                tv = scaledPos.Y * v.Normal.Z - scaledPos.Z * v.Normal.Y;
            }
            else 
            {
                tu = 2f * scaledPos.X;
                tv = -scaledPos.Y * v.Normal.Z + scaledPos.Z * v.Normal.Y;
            }

            tv *= 2f;
        }

        private static readonly UUID InvPrimMagicTexture = new UUID("e97cf410-8e61-7005-ec06-629eba4cd1fb");
        private void CreatePrim(WarpRenderer renderer, SceneObjectPart prim)
        {
            if (prim == null || prim.Shape == null)
                return;

            if ((PCode)prim.Shape.PCode != PCode.Prim)
                return;

            Vector3 ppos = prim.GetWorldPosition();
            if (ppos.Z < m_renderMinHeight || ppos.Z > m_renderMaxHeight)
                return;

            warp_Vector primPos = ConvertVector(ref ppos);
            warp_Matrix m = warp_Matrix.quaternionMatrix(ConvertQuaternion(prim.GetWorldRotation()));

            Vector3 primScale = prim.Scale;
            float screenFactor = renderer.Scene.EstimateBoxProjectedArea(primPos, ConvertVector(primScale), m);
            if (screenFactor < 0)
                return;

            int p2 = (int)(MathF.Log2(screenFactor) * 0.25f - 1);

            if (p2 < 0)
                p2 = 0;
            else if (p2 > 3)
                p2 = 3;

            DetailLevel lod = (DetailLevel)(3 - p2);

            FacetedMesh renderMesh = null;
            Primitive omvPrim = prim.Shape.ToOmvPrimitive(prim.OffsetPosition, prim.RotationOffset);
            bool externalGeometry = UsesExternalGeometry(omvPrim);

            if (m_renderMeshes && externalGeometry)
                renderMesh = TryGetExternalRenderMesh(omvPrim, prim, lod);

            // If not a mesh or sculptie, try the regular mesher
            if (renderMesh is null)
            {
                if (externalGeometry && m_renderMeshes && m_skipMissingExternalGeometry)
                {
                    m_missingGeometrySkipped++;
                    return;
                }

                renderMesh = m_primMesher.GenerateFacetedMesh(omvPrim, lod);
            }

            if (renderMesh is null)
                return;

            Primitive.TextureEntry te = prim.Shape.Textures;
            if (te is null)
                return;

            string primID = prim.UUID.ToString();

            float rc = 0;
            float rs = 0;
            bool flatTextureCard = IsLikelyFlatTextureCard(prim);
            int facesAdded = 0;

            for (int i = 0; i < renderMesh.Faces.Count; i++)
            {
                if (Map3DBudgetExpired())
                {
                    m_budgetSkipped++;
                    break;
                }

                Primitive.TextureEntryFace teFace = te.GetFace((uint)i);
                if (teFace is null)
                    teFace = te.DefaultTexture;
                if (teFace is null)
                    continue;

                Color4 faceColor = teFace.RGBA;
                if (faceColor.A == 0)
                    continue;

                if (faceColor.A == 1.0f && InvPrimMagicTexture.Equals(teFace.TextureID))
                    break;

                warp_Material faceMaterial;
                if (m_texturePrims)
                {
                    bool requireTexture = m_skipFlatTextureCardsWithoutTexture &&
                        flatTextureCard &&
                        !teFace.TextureID.IsZero();
                    faceMaterial = GetOrCreateMaterial(renderer, faceColor, teFace.TextureID, false, requireTexture, prim);
                    if (faceMaterial is null)
                    {
                        if (requireTexture)
                            m_flatTextureCardSkipped++;
                        continue;
                    }
                    if ((faceMaterial.getColor() & warp_Color.MASKALPHA) == 0)
                        continue;
                }
                else
                    faceMaterial = GetOrCreateMaterial(renderer, faceColor);

                warp_Object faceObj = new warp_Object();
                faceObj.setMaterial(faceMaterial);

                Face face = renderMesh.Faces[i];
                if (faceMaterial.getTexture() is null)
                {
                    // UV map details do not matter for flat color.
                    for (int j = 0; j < face.Vertices.Count; j++)
                    {
                        warp_Vector pos = ConvertVector(face.Vertices[j].Position);
                        warp_Vertex vert = new warp_Vertex(pos, face.Vertices[j].TexCoord.X, face.Vertices[j].TexCoord.Y);
                        faceObj.addVertex(vert);
                    }
                }
                else
                {
                    float tu;
                    float tv;
                    float offsetu = teFace.OffsetU + 0.5f;
                    float offsetv = teFace.OffsetV + 0.5f;
                    float scaleu = teFace.RepeatU;
                    float scalev = teFace.RepeatV;
                    float rotation = teFace.Rotation;
                    if (rotation != 0)
                    {
                        rc = MathF.Cos(rotation);
                        rs = MathF.Sin(rotation);
                    }

                    for (int j = 0; j < face.Vertices.Count; j++)
                    {
                        if(teFace.TexMapType == MappingType.Planar)
                        {
                            Vertex v = face.Vertices[j];
                            UVPlanarMap(ref v, ref primScale, out tu, out tv);
                        }
                        else
                        {
                            tu = face.Vertices[j].TexCoord.X - 0.5f;
                            tv = 0.5f - face.Vertices[j].TexCoord.Y;
                        }

                        warp_Vector pos = ConvertVector(face.Vertices[j].Position);
                        if (rotation != 0)
                        {
                            float tur = tu * rc - tv * rs;
                            float tvr = tu * rs + tv * rc;
                            faceObj.addVertex(new warp_Vertex(pos, tur * scaleu + offsetu, tvr * scalev + offsetv));
                        }
                        else
                        {
                            faceObj.addVertex(new warp_Vertex(pos, tu * scaleu + offsetu, tv * scalev + offsetv));
                        }
                    }
                }

                for (int j = 0; j + 2 < face.Indices.Count; j += 3)
                {
                    faceObj.addTriangle(
                        face.Indices[j + 0],
                        face.Indices[j + 1],
                        face.Indices[j + 2]);
                }

                faceObj.scaleSelf(primScale.X, primScale.Z, primScale.Y);
                faceObj.transform(m);
                faceObj.setPos(primPos);

                renderer.Scene.addObject(primID + i.ToString(), faceObj);
                facesAdded++;
                m_renderedFaces++;
            }

            if (facesAdded > 0)
                m_renderedParts++;
        }

        private FacetedMesh TryGetExternalRenderMesh(Primitive omvPrim, SceneObjectPart prim, DetailLevel lod)
        {
            if (omvPrim?.Sculpt is null || omvPrim.Sculpt.SculptTexture.IsZero())
                return null;

            if (Map3DBudgetExpired())
            {
                m_budgetSkipped++;
                return null;
            }

            string cacheKey = omvPrim.Sculpt.SculptTexture + ":" + omvPrim.Sculpt.Type + ":" + lod;
            if (m_failedGeometryAssets.Contains(cacheKey))
                return null;

            if (m_maxMeshAssetDecodes > 0 && m_meshAssetDecodesThisPass >= m_maxMeshAssetDecodes)
            {
                m_budgetSkipped++;
                return null;
            }

            byte[] sculptData = GetMapAssetData(omvPrim.Sculpt.SculptTexture);
            if (sculptData is null || sculptData.Length == 0)
            {
                m_failedGeometryAssets.Add(cacheKey);
                return null;
            }

            m_meshAssetDecodesThisPass++;
            FacetedMesh renderMesh = null;
            try
            {
                if ((((int)omvPrim.Sculpt.Type) & 0x07) == (int)SculptType.Mesh)
                {
                    FacetedMesh.TryDecodeFromBytes(sculptData, lod, out renderMesh, true);
                }
                else
                {
                    using (Image sculpt = DecodeSculptMapImage(sculptData))
                    {
                        if (sculpt is not null)
                        {
                            using (Bitmap sculptBitmap = new Bitmap(sculpt))
                                renderMesh = m_primMesher.GenerateFacetedSculptMesh(omvPrim, sculptBitmap, lod);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[Warp3D] failed to decode mesh/sculpt asset {0} of prim {1} at {2}: {3}",
                    omvPrim.Sculpt.SculptTexture, prim.Name, prim.GetWorldPosition(), e.Message);
            }

            if (renderMesh is null)
                m_failedGeometryAssets.Add(cacheKey);

            return renderMesh;
        }

        private bool Map3DBudgetExpired()
        {
            return m_renderTimeBudgetMS > 0 &&
                Util.EnvironmentTickCountSubtract(Environment.TickCount, m_renderStartMS) >= m_renderTimeBudgetMS;
        }

        private static bool UsesExternalGeometry(Primitive prim)
        {
            return prim != null &&
                prim.Sculpt != null &&
                prim.Sculpt.SculptTexture.IsNotZero() &&
                (((int)prim.Sculpt.Type) & 0x07) != (int)SculptType.None;
        }

        private static bool IsLikelyFlatTextureCard(SceneObjectPart part)
        {
            Vector3 scale = part.Scale;
            float min = Math.Min(scale.X, Math.Min(scale.Y, scale.Z));
            float max = Math.Max(scale.X, Math.Max(scale.Y, scale.Z));

            if (max < 1f)
                return false;

            float middle = scale.X + scale.Y + scale.Z - min - max;
            return min <= Math.Max(0.08f, middle * 0.08f);
        }

        private byte[] GetMapAssetData(UUID assetID)
        {
            if (assetID.IsZero())
                return null;

            try
            {
                AssetBase asset = m_useCachedAssetsOnly
                    ? m_scene.AssetService.GetCached(assetID.ToString())
                    : m_scene.AssetService.Get(assetID.ToString());

                if (asset != null && asset.Data != null && asset.Data.Length > 0)
                    return asset.Data;
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[Warp3D]: Asset fetch failed for map asset {0}: {1}", assetID, e.Message);
            }

            return null;
        }

        private int GetFaceColor(Primitive.TextureEntryFace face)
        {
            int color;
            Color4 ctmp = Color4.White;

            if (face.TextureID.IsZero())
                return warp_Color.White;

            if (!m_colors.TryGetValue(face.TextureID, out color))
            {
                bool fetched = false;

                // Attempt to fetch the texture metadata
                string cacheName = "MAPCLR" + face.TextureID.ToString();
                AssetBase metadata = m_scene.AssetService.GetCached(cacheName);
                if (metadata != null)
                {
                    OSDMap map = null;
                    try { map = OSDParser.Deserialize(metadata.Data) as OSDMap; } catch { }

                    if (map != null)
                    {
                        ctmp = map["X-RGBA"].AsColor4();
                        fetched = true;
                    }
                }

                if (!fetched)
                {
                    // Fetch the texture, decode and get the average color,
                    // then save it to a temporary metadata asset
                    AssetBase textureAsset = m_scene.AssetService.Get(face.TextureID.ToString());
                    if (textureAsset != null)
                    {
                        int width, height;
                        ctmp = GetAverageColor(textureAsset.FullID, textureAsset.Data, out width, out height);

                        OSDMap data = new OSDMap { { "X-RGBA", OSD.FromColor4(ctmp) } };
                        metadata = new AssetBase
                        {
                            Data = System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(data)),
                            Description = "Metadata for texture color" + face.TextureID.ToString(),
                            Flags = AssetFlags.Collectable,
                            FullID = UUID.Zero,
                            ID = cacheName,
                            Local = true,
                            Temporary = true,
                            Name = String.Empty,
                            Type = (sbyte)AssetType.Unknown
                        };
                        m_scene.AssetService.Store(metadata);
                    }
                    else
                    {
                        ctmp = new Color4(0.5f, 0.5f, 0.5f, 1.0f);
                    }
                }
                color = ConvertColor(ctmp);
                m_colors[face.TextureID] = color;
            }

            return color;
        }

        private warp_Material GetOrCreateMaterial(WarpRenderer renderer, Color4 color)
        {
            string name = color.ToString();

            if(renderer.Scene.TryGetMaterial(name, out warp_Material material))
                return material;

            material = new warp_Material(ConvertColor(color));
            renderer.Scene.addMaterial(name, material);
            return material;
        }

        public warp_Material GetOrCreateMaterial(WarpRenderer renderer, Color4 faceColor, UUID textureID,
            bool useAverageTextureColor, bool requireTexture, SceneObjectPart sop)
        {
            int color = ConvertColor(faceColor);
            string idstr = textureID.ToString() + color.ToString() +
                (useAverageTextureColor ? ":avg" : ":tex") +
                (requireTexture ? ":required" : ":fallback");
            string materialName = "MAPMAT" + idstr;

            if (renderer.Scene.TryGetMaterial(materialName, out warp_Material mat))
                return mat;

            mat = new warp_Material();
            warp_Texture texture = GetTexture(textureID, sop);
            if (texture is not null)
            {
                if (useAverageTextureColor)
                    color = warp_Color.multiply(color, texture.averageColor);
                else
                    mat.setTexture(texture);
            }
            else if (requireTexture)
            {
                return null;
            }
            else
                color = warp_Color.multiply(color, warp_Color.Grey);

            mat.setColor(color);
            renderer.Scene.addMaterial(materialName, mat);

            return mat;
        }

        private warp_Texture GetTexture(UUID id, SceneObjectPart sop)
        {
            if (id.IsZero())
                return null;
            if (m_warpTextures.TryGetValue(id, out warp_Texture ret))
                return ret;
            if (m_failedTextureAssets.Contains(id))
            {
                m_warpTextures[id] = null;
                return null;
            }
            if (m_maxTextureAssetDecodes > 0 && m_textureAssetDecodesThisPass >= m_maxTextureAssetDecodes)
            {
                m_warpTextures[id] = null;
                return null;
            }

            byte[] data = GetMapAssetData(id);
            if (data is not null && data.Length > 0)
            {
                m_textureAssetDecodesThisPass++;
                try
                {
                    using (Image image = DecodeTextureMapImage(data))
                    {
                        if (image is not null)
                        {
                            using (Bitmap img = new Bitmap(image))
                                ret = new warp_Texture(img, m_textureDownsample);
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[Warp3D]: Failed to decode texture {0} for prim {1} at {2}, exception {3}",
                        id.ToString(), sop.Name, sop.GetWorldPosition().ToString(), e.Message);
                }
            }

            if (ret is null)
                m_failedTextureAssets.Add(id);

            m_warpTextures[id] = ret;
            return ret;
        }

        private MapSpriteTexture GetSpriteTexture(UUID id, SceneObjectPart sop)
        {
            if (id.IsZero())
                return null;

            if (m_spriteTextures.TryGetValue(id, out MapSpriteTexture cached))
                return cached;

            if (m_failedTextureAssets.Contains(id))
            {
                m_spriteTextures[id] = null;
                return null;
            }

            if (m_maxSpriteTextureDecodes > 0 && m_spriteTextureDecodesThisPass >= m_maxSpriteTextureDecodes)
            {
                m_spriteTextures[id] = null;
                return null;
            }

            MapSpriteTexture sprite = null;
            byte[] data = GetMapAssetData(id);
            if (data is not null && data.Length > 0)
            {
                m_spriteTextureDecodesThisPass++;
                try
                {
                    using (Image image = DecodeTextureMapImage(data))
                    {
                        if (image is not null)
                            sprite = CreateSpriteTexture(image);
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[Warp3D]: Failed to decode texture-card sprite {0} for prim {1} at {2}, exception {3}",
                        id.ToString(), sop.Name, sop.GetWorldPosition().ToString(), e.Message);
                }
            }

            if (sprite is null)
                m_failedTextureAssets.Add(id);

            m_spriteTextures[id] = sprite;
            return sprite;
        }

        private MapSpriteTexture CreateSpriteTexture(Image image)
        {
            int width = image.Width;
            int height = image.Height;
            int largest = Math.Max(width, height);

            if (largest > m_spriteTextureMaxSize)
            {
                float scale = m_spriteTextureMaxSize / (float)largest;
                width = Math.Max(1, (int)MathF.Round(width * scale));
                height = Math.Max(1, (int)MathF.Round(height * scale));
            }

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.DrawImage(image, 0, 0, width, height);
            }

            GetSpriteAlphaCoverage(bitmap, out float alphaCoverage, out float opaqueCoverage);
            return new MapSpriteTexture
            {
                Bitmap = bitmap,
                AlphaCoverage = alphaCoverage,
                OpaqueCoverage = opaqueCoverage
            };
        }

        private static void GetSpriteAlphaCoverage(Bitmap bitmap, out float alphaCoverage, out float opaqueCoverage)
        {
            int visible = 0;
            int opaque = 0;
            int total = bitmap.Width * bitmap.Height;

            BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* row = (byte*)bitmapData.Scan0;
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        byte* pixel = row;
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            byte alpha = pixel[3];
                            if (alpha > 16)
                                visible++;
                            if (alpha > 240)
                                opaque++;
                            pixel += 4;
                        }
                        row += bitmapData.Stride;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            if (total <= 0)
            {
                alphaCoverage = 0f;
                opaqueCoverage = 0f;
                return;
            }

            alphaCoverage = visible / (float)total;
            opaqueCoverage = opaque / (float)total;
        }

        private bool TextureCardSpriteBudgetExpired()
        {
            return m_spriteRenderTimeBudgetMS > 0 &&
                Util.EnvironmentTickCountSubtract(Environment.TickCount, m_spriteStartMS) >= m_spriteRenderTimeBudgetMS;
        }

        private void DisposeSpriteTextures()
        {
            if (m_spriteTextures == null)
                return;

            foreach (MapSpriteTexture sprite in m_spriteTextures.Values)
                sprite?.Dispose();

            m_spriteTextures.Clear();
        }

        #endregion Rendering Methods

        #region Static Helpers
        // Note: axis change.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Vector ConvertVector(float x, float y, float z)
        {
            return new warp_Vector(x, z, y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Vector ConvertVector(Vector3 vector)
        {
            return new warp_Vector(vector.X, vector.Z, vector.Y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Vector ConvertVector(ref Vector3 vector)
        {
            return new warp_Vector(vector.X, vector.Z, vector.Y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Quaternion ConvertQuaternion(Quaternion quat)
        {
            return new warp_Quaternion(quat.X, quat.Z, quat.Y, -quat.W);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int ConvertColor(Color4 color)
        {
            int c = warp_Color.getColor((byte)(color.R * 255f), (byte)(color.G * 255f), (byte)(color.B * 255f), (byte)(color.A * 255f));
            return c;
        }

        private static float SmoothStep(float value)
        {
            return value * value * (3f - (2f * value));
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            float inverse = 1f - amount;

            return Color.FromArgb(
                ClampByte((int)((from.R * inverse) + (to.R * amount))),
                ClampByte((int)((from.G * inverse) + (to.G * amount))),
                ClampByte((int)((from.B * inverse) + (to.B * amount))));
        }

        private static int ClampByte(int value)
        {
            if (value < 0)
                return 0;

            if (value > 255)
                return 255;

            return value;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static Image DecodeSculptMapImage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            if (!LooksLikeJpeg2000(data))
                return null;

            try
            {
                return J2kImage.FromBytes(data, null, true, 12);
            }
            catch
            {
                return null;
            }
        }

        private static Image DecodeTextureMapImage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            if (!LooksLikeJpeg2000(data))
                return null;

            try
            {
                return J2kImage.FromBytes(data, null, false, 16);
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeJpeg2000(byte[] data)
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

            return HasJpeg2000EocMarker(data) && !HasUnknownJpeg2000CommentMarker(data);
        }

        private static bool HasJpeg2000EocMarker(byte[] data)
        {
            for (int i = data.Length - 2; i >= 0; i--)
            {
                if (data[i] == 0xff && data[i + 1] == 0xd9)
                    return true;
            }

            return false;
        }

        private static bool HasUnknownJpeg2000CommentMarker(byte[] data)
        {
            for (int i = 0; i + 5 < data.Length; i++)
            {
                if (data[i] != 0xff || data[i + 1] != 0x64)
                    continue;

                int registration = (data[i + 4] << 8) | data[i + 5];
                if (registration == 0)
                    return true;
            }

            return false;
        }

        private static Vector3 SurfaceNormal(Vector3 c1, Vector3 c2, Vector3 c3)
        {
            Vector3 normal = Vector3.Cross(c2 - c1, c3 - c1);
            normal.Normalize();

            return normal;
        }

        public Color4 GetAverageColor(UUID textureID, byte[] j2kData, out int width, out int height)
        {
            ulong r = 0;
            ulong g = 0;
            ulong b = 0;
            ulong a = 0;
            int pixelBytes;

            try
            {
                using (Image image = DecodeTextureMapImage(j2kData))
                {
                    if (image == null)
                        throw new InvalidDataException("invalid or unsupported JPEG2000 texture");

                    using (Bitmap bitmap = new Bitmap(image))
                    {
                        width = bitmap.Width;
                        height = bitmap.Height;

                        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
                        pixelBytes = (bitmapData.PixelFormat == PixelFormat.Format24bppRgb) ? 3 : 4;

                        // Sum up the individual channels
                        unsafe
                        {
                            byte* start = (byte*)bitmapData.Scan0;
                            if (pixelBytes == 4)
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    byte* end = start + 4 * width;
                                    for(byte* row = start; row < end; row += 4)
                                    {
                                        b += row[0];
                                        g += row[1];
                                        r += row[2];
                                        a += row[3];
                                    }
                                    start += bitmapData.Stride;
                                }
                            }
                            else
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    byte* end = start + 3 * width;
                                    for (byte* row = start; row < end; row += 3)
                                    {
                                        b += row[0];
                                        g += row[1];
                                        r += row[2];
                                    }
                                    start += bitmapData.Stride;
                                }
                            }
                        }
                        bitmap.UnlockBits(bitmapData);
                    }
                }
                // Get the averages for each channel
                double invtotalPixels = 1.0/(255.0 * width * height);
                double rm = r * invtotalPixels;
                double gm = g * invtotalPixels;
                double bm = b * invtotalPixels;
                double am = pixelBytes == 3 ? 1.0 : a * invtotalPixels;
                return new Color4((float)rm, (float)gm, (float)bm, (float)am);
            }
            catch (Exception ex)
            {
                m_log.DebugFormat(
                    "[WARP 3D IMAGE MODULE]: Error decoding JPEG2000 texture {0} ({1} bytes): {2}",
                    textureID, j2kData?.Length ?? 0, ex.Message);

                width = 0;
                height = 0;
                return new Color4(0.5f, 0.5f, 0.5f, 1.0f);
            }
        }

        #endregion Static Helpers
    }

    public static class ImageUtils
    {
        /// <summary>
        /// Performs bilinear interpolation between four values
        /// </summary>
        /// <param name="v00">First, or top left value</param>
        /// <param name="v01">Second, or top right value</param>
        /// <param name="v10">Third, or bottom left value</param>
        /// <param name="v11">Fourth, or bottom right value</param>
        /// <param name="xPercent">Interpolation value on the X axis, between 0.0 and 1.0</param>
        /// <param name="yPercent">Interpolation value on fht Y axis, between 0.0 and 1.0</param>
        /// <returns>The bilinearly interpolated result</returns>
        public static float Bilinear(float v00, float v01, float v10, float v11, float xPercent, float yPercent)
        {
            return Utils.Lerp(Utils.Lerp(v00, v01, xPercent), Utils.Lerp(v10, v11, xPercent), yPercent);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float Bilinear(float[] v, float xPercent, float yPercent)
        {
            return Utils.Lerp(Utils.Lerp(v[0], v[2], xPercent), Utils.Lerp(v[1], v[3], xPercent), yPercent);
        }
    }
}
