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

using log4net;
using Mono.Addins;
using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Grid
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionGridServicesConnector")]
    public class RegionGridServicesConnector : ISharedRegionModule, IGridService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_Enabled = false;
        private GridInfo m_ThisGridInfo;

        private IGridService m_LocalGridService;
        private IGridService m_RemoteGridService;

        private RegionInfoCache m_RegionInfoCache;
        private readonly List<MultiGridAttachment> m_MultiGridAttachments = new();
        private bool m_MultiGridEnabled = false;
        private bool m_MultiGridContinueOnFailure = true;

        public RegionGridServicesConnector()
        {
        }

        public RegionGridServicesConnector(IConfigSource source)
        {
            InitialiseServices(source);
        }

        #region ISharedRegionmodule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "RegionGridServicesConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("GridServices", string.Empty);
                if (name == Name)
                {
                    if(InitialiseServices(source))
                    {
                        m_Enabled = true;
                        if(m_RemoteGridService == null)
                            m_log.Info("[REGION GRID CONNECTOR]: enabled in Standalone mode");
                        else
                            m_log.Info("[REGION GRID CONNECTOR]: enabled in Grid mode");
                    }
                }
            }
        }

        private bool InitialiseServices(IConfigSource source)
        {
            IConfig gridConfig = source.Configs["GridService"];
            if (gridConfig == null)
            {
                m_log.Error("[REGION GRID CONNECTOR]: GridService missing from OpenSim.ini");
                return false;
            }

            string serviceDll = gridConfig.GetString("LocalServiceModule", string.Empty);
            if (string.IsNullOrWhiteSpace(serviceDll))
            {
                m_log.Error("[REGION GRID CONNECTOR]: No LocalServiceModule named in section GridService");
                return false;
            }
            
            object[] args = new object[] { source };
            m_LocalGridService = ServerUtils.LoadPlugin<IGridService>(serviceDll, args);

            if (m_LocalGridService == null)
            {
                m_log.Error("[REGION GRID CONNECTOR]: failed to load LocalServiceModule");
                return false;
            }

            string networkConnector = gridConfig.GetString("NetworkConnector", string.Empty);
            if (!string.IsNullOrWhiteSpace(networkConnector))
            {
                m_RemoteGridService = ServerUtils.LoadPlugin<IGridService>(networkConnector, args);
                if (m_RemoteGridService == null)
                {
                    m_log.Error("[REGION GRID CONNECTOR]: failed to load NetworkConnector");
                    return false;
                }
            }

            m_RegionInfoCache = new RegionInfoCache();
            InitialiseMultiGridAttachments(source);
            return true;
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            m_ThisGridInfo = null;
        }

        public void AddRegion(Scene scene)
        {
            if (m_Enabled)
            {
                scene.RegisterModuleInterface<IGridService>(this);
                if(m_ThisGridInfo == null)
                    m_ThisGridInfo = scene.SceneGridInfo;

                GridRegion r = new GridRegion(scene.RegionInfo);
                m_RegionInfoCache.CacheLocal(r);

                scene.EventManager.OnRegionUp += OnRegionUp;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_Enabled)
            {
                m_RegionInfoCache.Remove(scene.RegionInfo.ScopeID, scene.RegionInfo.RegionHandle);
                scene.EventManager.OnRegionUp -= OnRegionUp;
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion

        private void OnRegionUp(GridRegion region)
        {
            // This shouldn't happen
            if (region == null || !m_Enabled)
                return;

            m_RegionInfoCache.CacheNearNeighbour(region.ScopeID, region);
        }

        #region IGridService

        public string RegisterRegion(UUID scopeID, GridRegion regionInfo)
        {
            string msg = m_LocalGridService.RegisterRegion(scopeID, regionInfo);
            if (msg.Length == 0)
            {
                if (m_RemoteGridService != null)
                    msg = m_RemoteGridService.RegisterRegion(scopeID, regionInfo);

                if (msg.Length == 0)
                    RegisterRegionWithMultiGridAttachments(scopeID, regionInfo);
            }

            return msg;
        }

        public bool DeregisterRegion(UUID regionID)
        {
            if (m_LocalGridService.DeregisterRegion(regionID))
            {
                if (m_RemoteGridService != null)
                {
                    bool remoteOk = m_RemoteGridService.DeregisterRegion(regionID);
                    DeregisterRegionFromMultiGridAttachments(regionID);
                    return remoteOk;
                }
                DeregisterRegionFromMultiGridAttachments(regionID);
                return true;
            }

            return false;
        }

        private void InitialiseMultiGridAttachments(IConfigSource source)
        {
            IConfig config = source.Configs["MultiGridAttachments"];
            if (config == null)
                return;

            m_MultiGridEnabled = config.GetBoolean("Enabled", false);
            if (!m_MultiGridEnabled)
                return;

            m_MultiGridContinueOnFailure = config.GetBoolean("ContinueOnFailure", true);

            string gridList = config.GetString("Grids", string.Empty);
            foreach (string rawName in gridList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = rawName.Trim();
                if (name.Length == 0)
                    continue;

                IConfig attachmentConfig = FindMultiGridAttachmentConfig(source, name);
                if (attachmentConfig == null)
                {
                    m_log.WarnFormat("[REGION GRID CONNECTOR]: MultiGrid attachment {0} has no config section", name);
                    continue;
                }

                MultiGridAttachment attachment = MultiGridAttachment.FromConfig(name, attachmentConfig);
                if (!attachment.Enabled)
                    continue;

                if (attachment.GridServerURI.Length == 0)
                {
                    m_log.WarnFormat("[REGION GRID CONNECTOR]: MultiGrid attachment {0} has no GridServerURI", attachment.Name);
                    continue;
                }

                m_MultiGridAttachments.Add(attachment);
            }

            if (m_MultiGridAttachments.Count > 0)
                m_log.InfoFormat("[REGION GRID CONNECTOR]: enabled {0} MultiGrid attachment(s)", m_MultiGridAttachments.Count);
        }

        private static IConfig FindMultiGridAttachmentConfig(IConfigSource source, string name)
        {
            return source.Configs["MultiGridAttachment " + name]
                ?? source.Configs["MultiGridAttachment:" + name]
                ?? source.Configs["MultiGridAttachment." + name];
        }

        private void RegisterRegionWithMultiGridAttachments(UUID primaryScopeID, GridRegion regionInfo)
        {
            if (!m_MultiGridEnabled || m_MultiGridAttachments.Count == 0)
                return;

            foreach (MultiGridAttachment attachment in m_MultiGridAttachments)
            {
                if (!attachment.MatchesRegion(regionInfo))
                    continue;

                string error = RegisterRegionWithMultiGridAttachment(primaryScopeID, regionInfo, attachment);
                if (error.Length == 0)
                    continue;

                if (!m_MultiGridContinueOnFailure || attachment.Strict)
                    throw new Exception(error);
            }
        }

        private string RegisterRegionWithMultiGridAttachment(UUID primaryScopeID, GridRegion regionInfo, MultiGridAttachment attachment)
        {
            GridRegion attachedRegion = attachment.Apply(regionInfo);
            UUID scopeID = attachment.ScopeID.IsZero() ? primaryScopeID : attachment.ScopeID;

            Dictionary<string, object> rinfo = attachedRegion.ToKeyValuePairs();
            Dictionary<string, object> sendData = new()
            {
                ["SCOPEID"] = scopeID.ToString(),
                ["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString(),
                ["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString(),
                ["METHOD"] = "register"
            };

            foreach (KeyValuePair<string, object> kvp in rinfo)
                sendData[kvp.Key] = (string)kvp.Value;

            string endpoint = attachment.GridPostURI;
            try
            {
                string reply = SynchronousRestFormsRequester.MakePostRequest(
                    endpoint,
                    ServerUtils.BuildQueryString(sendData),
                    attachment.Auth);

                if (reply.Length > 0)
                {
                    if (!LooksLikeGridServiceXmlReply(reply))
                    {
                        string nonGridReply = string.Format(
                            "MultiGrid attachment {0} returned non-grid-service reply from {1}: {2}",
                            attachment.Name, endpoint, AbbreviateForLog(reply, 140));
                        m_log.ErrorFormat("[REGION GRID CONNECTOR]: {0}", nonGridReply);
                        return nonGridReply;
                    }

                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
                    if (replyData.TryGetValue("Result", out object tmpo) && tmpo is string result)
                    {
                        if (result.Equals("success", StringComparison.CurrentCultureIgnoreCase))
                        {
                            m_log.InfoFormat(
                                "[REGION GRID CONNECTOR]: MultiGrid attachment {0} registered region {1} at {2}",
                                attachment.Name, attachedRegion.RegionName, endpoint);
                            return string.Empty;
                        }

                        if (result.Equals("failure", StringComparison.CurrentCultureIgnoreCase))
                        {
                            string message = replyData.TryGetValue("Message", out object msg) ? msg.ToString() : "unknown failure";
                            string error = string.Format(
                                "MultiGrid attachment {0} registration failed: {1}",
                                attachment.Name, message);
                            m_log.ErrorFormat("[REGION GRID CONNECTOR]: {0}", error);
                            return error;
                        }

                        string unexpected = string.Format(
                            "MultiGrid attachment {0} returned unexpected result {1}",
                            attachment.Name, result);
                        m_log.ErrorFormat("[REGION GRID CONNECTOR]: {0}", unexpected);
                        return unexpected;
                    }

                    string missing = string.Format(
                        "MultiGrid attachment {0} reply did not contain Result",
                        attachment.Name);
                    m_log.ErrorFormat("[REGION GRID CONNECTOR]: {0}", missing);
                    return missing;
                }

                string empty = string.Format(
                    "MultiGrid attachment {0} received empty reply from {1}",
                    attachment.Name, endpoint);
                m_log.ErrorFormat("[REGION GRID CONNECTOR]: {0}", empty);
                return empty;
            }
            catch (Exception e)
            {
                string error = string.Format(
                    "MultiGrid attachment {0} exception at {1}: {2}",
                    attachment.Name, endpoint, e.Message);
                m_log.ErrorFormat("[REGION GRID CONNECTOR]: {0}", error);
                return error;
            }
        }

        private static bool LooksLikeGridServiceXmlReply(string reply)
        {
            string trimmed = reply.TrimStart();
            return trimmed.StartsWith("<ServerResponse", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        }

        private static string AbbreviateForLog(string value, int maxLength)
        {
            string compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            while (compact.Contains("  "))
                compact = compact.Replace("  ", " ");

            if (compact.Length <= maxLength)
                return compact;

            return compact.Substring(0, maxLength) + "...";
        }

        private void DeregisterRegionFromMultiGridAttachments(UUID regionID)
        {
            if (!m_MultiGridEnabled || m_MultiGridAttachments.Count == 0)
                return;

            foreach (MultiGridAttachment attachment in m_MultiGridAttachments)
            {
                Dictionary<string, object> sendData = new()
                {
                    ["REGIONID"] = regionID.ToString(),
                    ["METHOD"] = "deregister"
                };

                try
                {
                    string reply = SynchronousRestFormsRequester.MakePostRequest(
                        attachment.GridPostURI,
                        ServerUtils.BuildQueryString(sendData),
                        attachment.Auth);

                    if (reply.Length == 0)
                    {
                        m_log.WarnFormat(
                            "[REGION GRID CONNECTOR]: MultiGrid attachment {0} received empty deregister reply from {1}",
                            attachment.Name, attachment.GridPostURI);
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat(
                        "[REGION GRID CONNECTOR]: MultiGrid attachment {0} deregister exception at {1}: {2}",
                        attachment.Name, attachment.GridPostURI, e.Message);
                }
            }
        }

        private sealed class MultiGridAttachment
        {
            public string Name;
            public bool Enabled;
            public bool Strict;
            public string GridServerURI;
            public string GridPostURI;
            public string ExternalHostName;
            public string ServerURI;
            public string RegionName;
            public string RegionNamePrefix;
            public string RegionNameSuffix;
            public int? LocationX;
            public int? LocationY;
            public int? WorldLocationX;
            public int? WorldLocationY;
            public uint? HttpPort;
            public UUID ScopeID;
            public IServiceAuth Auth;
            private readonly HashSet<string> m_Regions = new(StringComparer.OrdinalIgnoreCase);

            public static MultiGridAttachment FromConfig(string name, IConfig config)
            {
                MultiGridAttachment attachment = new()
                {
                    Name = name,
                    Enabled = config.GetBoolean("Enabled", true),
                    Strict = config.GetBoolean("Strict", false),
                    GridServerURI = NormalizeServerURI(config.GetString("GridServerURI", string.Empty)),
                    ExternalHostName = config.GetString("ExternalHostName", string.Empty).Trim(),
                    ServerURI = NormalizeOptionalURI(config.GetString("ServerURI", string.Empty)),
                    RegionName = config.GetString("RegionName", string.Empty).Trim(),
                    RegionNamePrefix = config.GetString("RegionNamePrefix", string.Empty),
                    RegionNameSuffix = config.GetString("RegionNameSuffix", string.Empty),
                    ScopeID = UUID.Zero
                };

                string scope = config.GetString("ScopeID", string.Empty).Trim();
                if (scope.Length > 0)
                    UUID.TryParse(scope, out attachment.ScopeID);

                if (TryParsePair(config.GetString("Location", string.Empty), out int locX, out int locY))
                {
                    attachment.LocationX = locX;
                    attachment.LocationY = locY;
                }

                if (TryParsePair(config.GetString("WorldLocation", string.Empty), out int worldX, out int worldY))
                {
                    attachment.WorldLocationX = worldX;
                    attachment.WorldLocationY = worldY;
                }

                int httpPort = config.GetInt("HttpPort", -1);
                if (httpPort >= 0)
                    attachment.HttpPort = (uint)httpPort;

                foreach (string region in config.GetString("Regions", string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = region.Trim();
                    if (trimmed.Length > 0)
                        attachment.m_Regions.Add(trimmed);
                }

                attachment.GridPostURI = attachment.GridServerURI.EndsWith("/grid", StringComparison.OrdinalIgnoreCase)
                    ? attachment.GridServerURI
                    : attachment.GridServerURI + "/grid";

                attachment.Auth = BuildAuth(config);
                return attachment;
            }

            public bool MatchesRegion(GridRegion region)
            {
                if (m_Regions.Count == 0)
                    return true;

                return m_Regions.Contains(region.RegionName) || m_Regions.Contains(region.RegionID.ToString());
            }

            public GridRegion Apply(GridRegion source)
            {
                GridRegion region = new(source);

                if (RegionName.Length > 0)
                    region.RegionName = RegionName;
                else if (RegionNamePrefix.Length > 0 || RegionNameSuffix.Length > 0)
                    region.RegionName = RegionNamePrefix + region.RegionName + RegionNameSuffix;

                if (WorldLocationX.HasValue && WorldLocationY.HasValue)
                {
                    region.RegionLocX = WorldLocationX.Value;
                    region.RegionLocY = WorldLocationY.Value;
                }
                else if (LocationX.HasValue && LocationY.HasValue)
                {
                    region.RegionLocX = (int)Util.RegionToWorldLoc((uint)LocationX.Value);
                    region.RegionLocY = (int)Util.RegionToWorldLoc((uint)LocationY.Value);
                }

                if (ExternalHostName.Length > 0)
                    region.ExternalHostName = ExternalHostName;

                if (HttpPort.HasValue)
                    region.HttpPort = HttpPort.Value;

                if (ServerURI.Length > 0)
                    region.ServerURI = ServerURI;
                else if (ExternalHostName.Length > 0 || HttpPort.HasValue)
                    region.RawServerURI = string.Empty;

                return region;
            }

            private static IServiceAuth BuildAuth(IConfig config)
            {
                string authType = config.GetString("AuthType", "None");
                if (!authType.Equals("BasicHttpAuthentication", StringComparison.OrdinalIgnoreCase))
                    return null;

                IniConfigSource authConfig = new();
                IConfig authSection = authConfig.AddConfig("GridService");
                authSection.Set("AuthType", "BasicHttpAuthentication");
                authSection.Set("HttpAuthUsername", config.GetString("HttpAuthUsername", string.Empty));
                authSection.Set("HttpAuthPassword", config.GetString("HttpAuthPassword", string.Empty));
                return new BasicHttpAuthentication(authConfig, "GridService");
            }

            private static bool TryParsePair(string value, out int x, out int y)
            {
                x = 0;
                y = 0;

                if (string.IsNullOrWhiteSpace(value))
                    return false;

                string[] parts = value.Split(',');
                if (parts.Length != 2)
                    return false;

                return int.TryParse(parts[0].Trim(), out x) && int.TryParse(parts[1].Trim(), out y);
            }

            private static string NormalizeServerURI(string uri)
            {
                uri = NormalizeOptionalURI(uri);
                if (uri.EndsWith("/grid", StringComparison.OrdinalIgnoreCase))
                    return uri.Substring(0, uri.Length - 5);
                return uri;
            }

            private static string NormalizeOptionalURI(string uri)
            {
                uri = uri.Trim();
                if (uri.Length == 0)
                    return string.Empty;
                return uri.EndsWith("/") ? uri.TrimEnd('/') : uri;
            }
        }

        public List<GridRegion> GetNeighbours(UUID scopeID, UUID regionID)
        {
            if(m_RemoteGridService == null)
                return m_LocalGridService.GetNeighbours(scopeID, regionID);
            return m_RemoteGridService.GetNeighbours(scopeID, regionID);
        }

        public GridRegion GetRegionByUUID(UUID scopeID, UUID regionID)
        {
            bool inCache = false;
            GridRegion rinfo = m_RegionInfoCache.Get(scopeID, regionID, out inCache);
            if (inCache)
                return rinfo;

            rinfo = m_LocalGridService.GetRegionByUUID(scopeID, regionID);
            if (rinfo != null)
            {
                m_RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }

            if(m_RemoteGridService != null)
            {
                rinfo = m_RemoteGridService.GetRegionByUUID(scopeID, regionID);
                m_RegionInfoCache.Cache(scopeID, rinfo);
            }
            return rinfo;
        }

        public GridRegion GetRegionByHandle(UUID scopeID, ulong regionhandle)
        {
            bool inCache = false;
            GridRegion rinfo = m_RegionInfoCache.Get(scopeID, regionhandle, out inCache);
            if (inCache)
                return rinfo;

            rinfo = m_LocalGridService.GetRegionByHandle(scopeID, regionhandle);
            if (rinfo != null)
            {
                m_RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }
            if(m_RemoteGridService != null)
            {
                rinfo = m_RemoteGridService.GetRegionByHandle(scopeID, regionhandle);
                m_RegionInfoCache.Cache(scopeID, rinfo);
            }
            return rinfo;
        }

        // Get a region given its base world coordinates (in meters).
        // NOTE: this is NOT 'get a region by some point in the region'. The coordinate MUST
        //     be the base coordinate of the region.
        // The coordinates are world coords (meters), NOT region units.
        public GridRegion GetRegionByPosition(UUID scopeID, int x, int y)
        {
            bool inCache = false;
            GridRegion rinfo = m_RegionInfoCache.Get(scopeID, (uint)x, (uint)y, out inCache);
            if (inCache)
                return rinfo;

            rinfo = m_LocalGridService.GetRegionByPosition(scopeID, x, y);
            if (rinfo != null)
            {
                // m_log.DebugFormat("[REMOTE GRID CONNECTOR]: GetRegionByPosition. Found region {0} on local. Pos=<{1},{2}>, RegionHandle={3}",
                //    rinfo.RegionName, rinfo.RegionCoordX, rinfo.RegionCoordY, rinfo.RegionHandle);
                m_RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }

            if(m_RemoteGridService != null)
            {
                rinfo = m_RemoteGridService.GetRegionByPosition(scopeID, x, y);
                if (rinfo == null)
                {
    //                uint regionX = Util.WorldToRegionLoc((uint)x);
    //                uint regionY = Util.WorldToRegionLoc((uint)y);
    //                m_log.WarnFormat("[REMOTE GRID CONNECTOR]: Requested region {0}-{1} not found", regionX, regionY);
                }
                else
                {
                    m_RegionInfoCache.Cache(scopeID, rinfo);

    //                m_log.DebugFormat("[REMOTE GRID CONNECTOR]: GetRegionByPosition. Added region {0} to the cache. Pos=<{1},{2}>, RegionHandle={3}",
    //                    rinfo.RegionName, rinfo.RegionCoordX, rinfo.RegionCoordY, rinfo.RegionHandle);
                }
            }
            return rinfo;
        }

        public GridRegion GetRegionByName(UUID scopeID, string name)
        {
            bool inCache = false;
            GridRegion rinfo = m_RegionInfoCache.Get(scopeID, name, out inCache);
            if (inCache)
                return rinfo;

            var ruri = new RegionURI(name, m_ThisGridInfo);
            return GetRegionByURI(scopeID, ruri);
        }

        public GridRegion GetRegionByURI(UUID scopeID, RegionURI uri)
        {
            if(!uri.IsValid)
                return null;
            GridRegion rinfo = m_LocalGridService.GetRegionByURI(scopeID, uri);
            if (rinfo != null)
            {
                m_RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }

            if (m_RemoteGridService == null || !uri.IsLocalGrid)
                return rinfo;

            if (uri.HasRegionName)
                rinfo = m_RemoteGridService.GetRegionByName(scopeID, uri.RegionName);
            else
            {
                rinfo = m_RemoteGridService.GetDefaultRegions(UUID.Zero)[0];
                if (rinfo == null)
                    m_log.Warn("[REMOTE GRID CONNECTOR] returned null default region");
                else
                    m_log.WarnFormat("[REMOTE GRID CONNECTOR] returned default region {0}", rinfo.RegionName);
            }

            m_RegionInfoCache.Cache(scopeID, rinfo);
            return rinfo;
        }

        public GridRegion GetLocalRegionByName(UUID scopeID, string name)
        {
            return null;
        }

        public GridRegion GetLocalRegionByURI(UUID scopeID, RegionURI uri)
        {
            return null;
        }

        public List<GridRegion> GetRegionsByName(UUID scopeID, string name, int maxNumber)
        {
            var ruri = new RegionURI(name, m_ThisGridInfo);
            return GetRegionsByURI(scopeID, ruri, maxNumber);
        }

        public List<GridRegion> GetRegionsByURI(UUID scopeID, RegionURI uri, int maxNumber)
        {
            if(!uri.IsValid)
                return null;

            List<GridRegion> rinfo = m_LocalGridService.GetRegionsByURI(scopeID, uri, maxNumber);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetRegionsByName {0} found {1} regions", name, rinfo.Count);

            if (m_RemoteGridService == null || !uri.IsLocalGrid)
                return rinfo;

            List<GridRegion> grinfo = null;
            if (!uri.HasRegionName && (rinfo == null || rinfo.Count == 0))
            {
                List<GridRegion> grinfos = m_RemoteGridService.GetDefaultRegions(scopeID);
                if (grinfos == null || grinfos.Count == 0)
                    m_log.Info("[REMOTE GRID CONNECTOR] returned no default regions");
                else
                {
                    m_log.InfoFormat("[REMOTE GRID CONNECTOR] returned default regions {0}, ...", grinfos[0].RegionName);
                    // only return first
                    grinfo = new List<GridRegion>() { grinfos[0] };
                }
            }
            else
                grinfo = m_RemoteGridService.GetRegionsByName(scopeID, uri.RegionName, maxNumber);

            if (grinfo != null)
            {
                //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetRegionsByName {0} found {1} regions", name, grinfo.Count);
                foreach (GridRegion r in grinfo)
                {
                    m_RegionInfoCache.Cache(r);
                    if (rinfo.Find(delegate (GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                        rinfo.Add(r);
                }
            }

            return rinfo;
        }

        public virtual List<GridRegion> GetRegionRange(UUID scopeID, int xmin, int xmax, int ymin, int ymax)
        {
            List<GridRegion> rinfo = m_LocalGridService.GetRegionRange(scopeID, xmin, xmax, ymin, ymax);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetRegionRange {0} found {1} regions", name, rinfo.Count);
            if(m_RemoteGridService != null)
            {
                List<GridRegion> grinfo = m_RemoteGridService.GetRegionRange(scopeID, xmin, xmax, ymin, ymax);

                if (grinfo != null)
                {
                    //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetRegionRange {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        m_RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetDefaultRegions(UUID scopeID)
        {
            List<GridRegion> rinfo = m_LocalGridService.GetDefaultRegions(scopeID);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetDefaultRegions {0} found {1} regions", name, rinfo.Count);
            if(m_RemoteGridService != null)
            {
                List<GridRegion> grinfo = m_RemoteGridService.GetDefaultRegions(scopeID);

                if (grinfo != null)
                {
                    //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetDefaultRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        m_RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetDefaultHypergridRegions(UUID scopeID)
        {
            List<GridRegion> rinfo = m_LocalGridService.GetDefaultHypergridRegions(scopeID);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetDefaultHypergridRegions {0} found {1} regions", name, rinfo.Count);
            if(m_RemoteGridService != null)
            {
                List<GridRegion> grinfo = m_RemoteGridService.GetDefaultHypergridRegions(scopeID);

                if (grinfo != null)
                {
                    //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetDefaultHypergridRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        m_RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetFallbackRegions(UUID scopeID, int x, int y)
        {
            List<GridRegion> rinfo = m_LocalGridService.GetFallbackRegions(scopeID, x, y);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetFallbackRegions {0} found {1} regions", name, rinfo.Count);
            if (m_RemoteGridService != null)
            {
                List<GridRegion> grinfo = m_RemoteGridService.GetFallbackRegions(scopeID, x, y);

                if (grinfo != null)
                {
                    //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetFallbackRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        m_RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate (GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetOnlineRegions(UUID scopeID, int x, int y, int maxCount)
        {
            List<GridRegion> rinfo = m_LocalGridService.GetOnlineRegions(scopeID, x, y, maxCount);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetFallbackRegions {0} found {1} regions", name, rinfo.Count);
            if (m_RemoteGridService != null)
            {
                List<GridRegion> grinfo = m_RemoteGridService.GetOnlineRegions(scopeID, x, y, maxCount);

                if (grinfo != null)
                {
                    //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetOnlineRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        m_RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate (GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetHyperlinks(UUID scopeID)
        {
            List<GridRegion> rinfo = m_LocalGridService.GetHyperlinks(scopeID);
            //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetHyperlinks {0} found {1} regions", name, rinfo.Count);
            if(m_RemoteGridService != null)
            {
                List<GridRegion> grinfo = m_RemoteGridService.GetHyperlinks(scopeID);

                if (grinfo != null)
                {
                    //m_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetHyperlinks {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        m_RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public int GetRegionFlags(UUID scopeID, UUID regionID)
        {
            int flags = m_LocalGridService.GetRegionFlags(scopeID, regionID);
            if (flags == -1 && m_RemoteGridService != null)
                flags = m_RemoteGridService.GetRegionFlags(scopeID, regionID);

            return flags;
        }

        public Dictionary<string, object> GetExtraFeatures()
        {
            Dictionary<string, object> extraFeatures;
            extraFeatures = m_LocalGridService.GetExtraFeatures();

            if (extraFeatures.Count == 0 && m_RemoteGridService != null)
                extraFeatures = m_RemoteGridService.GetExtraFeatures();

            return extraFeatures;
        }
        #endregion
    }
}
