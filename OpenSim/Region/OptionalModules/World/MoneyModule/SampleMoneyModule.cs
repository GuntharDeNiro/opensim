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
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using Mono.Addins;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.World.MoneyModule
{
    /// <summary>
    /// This module provides a small local economy implementation for standalone
    /// and lightweight grid deployments. It stores avatar balances locally,
    /// answers viewer balance requests and supports simple in-region transfers.
    ///  // To land transfer you need to add:
    /// -helperuri http://serveraddress:port/
    /// to the command line parameters you use to start up your client
    /// This commonly looks like -helperuri http://127.0.0.1:9000/
    ///
    /// </summary>

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SampleMoneyModule")]
    public class SampleMoneyModule : IMoneyModule, ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Where Stipends come from and Fees go to.
        /// </summary>
        // private UUID EconomyBaseAccount = UUID.Zero;

        private Dictionary<string, XmlRpcMethod> m_rpcHandlers;
        private string m_localEconomyURL;
        private readonly object m_balanceLock = new object();
        private readonly Dictionary<UUID, int> m_balances = new Dictionary<UUID, int>();
        private string m_balanceStoragePath = "Currency/balances.tsv";
        private bool m_balancesLoaded;
        private bool m_allowNegativeBalances;
        private int m_initialBalance = 1000;

        private float EnergyEfficiency = 1f;
        // private ObjectPaid handerOnObjectPaid;
        private bool m_enabled = true;
        private bool m_sellEnabled = true;

        private IConfigSource m_gConfig;

        /// <summary>
        /// Region UUIDS indexed by AgentID
        /// </summary>

        /// <summary>
        /// Scenes by Region Handle
        /// </summary>
        private Dictionary<ulong, Scene> m_scenes = new Dictionary<ulong, Scene>();

        // private int m_stipend = 1000;

        private int ObjectCount = 0;
        private int PriceEnergyUnit = 0;
        private int PriceGroupCreate = -1;
        private int PriceObjectClaim = 0;
        private float PriceObjectRent = 0f;
        private float PriceObjectScaleFactor = 10f;
        private int PriceParcelClaim = 0;
        private float PriceParcelClaimFactor = 1f;
        private int PriceParcelRent = 0;
        private int PricePublicObjectDecay = 0;
        private int PricePublicObjectDelete = 0;
        private int PriceRentLight = 0;
        private int PriceUpload = 0;
        private int TeleportMinPrice = 0;

        private float TeleportPriceExponent = 2f;


        #region IMoneyModule Members

#pragma warning disable 0067
        public event ObjectPaid OnObjectPaid;
#pragma warning restore 0067

        public int UploadCharge
        {
            get { return PriceUpload; }
        }

        public int GroupCreationCharge
        {
            get { return Math.Max(0, PriceGroupCreate); }
        }

        /// <summary>
        /// Called on startup so the module can be configured.
        /// </summary>
        /// <param name="config">Configuration source.</param>
        public void Initialise(IConfigSource config)
        {
            m_gConfig = config;
            ReadConfigAndPopulate();
        }

        public void AddRegion(Scene scene)
        {
            if (m_enabled)
            {
                scene.RegisterModuleInterface<IMoneyModule>(this);
                IHttpServer httpServer = MainServer.Instance;

                lock (m_scenes)
                {
                    if (m_scenes.Count == 0)
                    {
                        m_localEconomyURL = scene.RegionInfo.ServerURI;
                        m_rpcHandlers = new Dictionary<string, XmlRpcMethod>();
                        m_rpcHandlers.Add("getCurrencyQuote", quote_func);
                        m_rpcHandlers.Add("buyCurrency", buy_func);
                        m_rpcHandlers.Add("preflightBuyLandPrep", preflightBuyLandPrep_func);
                        m_rpcHandlers.Add("buyLandPrep", landBuy_func);

                        // add php
                        MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/currency.php", processPHP));
                        MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/landtool.php", processPHP));
                    }

                    if (m_scenes.ContainsKey(scene.RegionInfo.RegionHandle))
                    {
                        m_scenes[scene.RegionInfo.RegionHandle] = scene;
                    }
                    else
                    {
                        m_scenes.Add(scene.RegionInfo.RegionHandle, scene);
                    }
                }

                scene.EventManager.OnNewClient += OnNewClient;
                scene.EventManager.OnMoneyTransfer += MoneyTransferAction;
                scene.EventManager.OnClientClosed += ClientClosed;
                scene.EventManager.OnAvatarEnteringNewParcel += AvatarEnteringParcel;
                scene.EventManager.OnMakeChildAgent += MakeChildAgent;
                scene.EventManager.OnValidateLandBuy += ValidateLandBuy;
                scene.EventManager.OnLandBuy += processLandBuy;
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;
            if(scene.SceneGridInfo!= null && !string.IsNullOrEmpty(scene.SceneGridInfo.EconomyURL))
                return;
            ISimulatorFeaturesModule fm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            if (fm != null && !string.IsNullOrWhiteSpace(m_localEconomyURL))
            {
                if(fm.TryGetOpenSimExtraFeature("currency-base-uri", out OSD tmp))
                    return;
                fm.AddOpenSimExtraFeature("currency-base-uri", Util.AppendEndSlash(m_localEconomyURL));
            }
        }

        public void processPHP(IOSHttpRequest request, IOSHttpResponse response)
        {
            MainServer.Instance.HandleXmlRpcRequests((OSHttpRequest)request, (OSHttpResponse)response, m_rpcHandlers);
        }

        // Please do not refactor these to be just one method
        // Existing implementations need the distinction
        //
        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData)
        {
            if (amount <= 0)
                return;

            string description = string.IsNullOrWhiteSpace(extraData) ? type.ToString() : extraData;
            bool result = Debit(agentID, amount, out string reason);
            SendBalanceUpdateTo(agentID, agentID, UUID.Zero, result, result ? description : reason, (int)type, amount);
            if (!result && !string.IsNullOrWhiteSpace(reason))
            {
                IClientAPI client = LocateClientObject(agentID);
                client?.SendAgentAlertMessage(reason, false);
            }
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type)
        {
            ApplyCharge(agentID, amount, type, String.Empty);
        }

        public void ApplyUploadCharge(UUID agentID, int amount, string text)
        {
            if (amount <= 0)
                return;

            string description = string.IsNullOrWhiteSpace(text) ? "Asset upload" : text;
            bool result = Debit(agentID, amount, out string reason);
            SendBalanceUpdateTo(agentID, agentID, UUID.Zero, result, result ? description : reason, 0, amount);
            if (!result && !string.IsNullOrWhiteSpace(reason))
            {
                IClientAPI client = LocateClientObject(agentID);
                client?.SendAgentAlertMessage(reason, false);
            }
        }

        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string result)
        {
            result = String.Empty;
            string description = String.Format("Object {0} pays {1}", resolveObjectName(objectID), resolveAgentName(toID));

            bool give_result = doMoneyTransfer(fromID, toID, amount, (int)TransactionType.Gift, description, out result);


            BalanceUpdate(fromID, toID, give_result, description, (int)TransactionType.Gift, amount);

            return give_result;
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface
        {
            get { return typeof(IMoneyModule); }
        }

        public string Name
        {
            get { return "BetaGridLikeMoneyModule"; }
        }

        #endregion

        /// <summary>
        /// Parse Configuration
        /// </summary>
        private void ReadConfigAndPopulate()
        {
            // we are enabled by default

            IConfig startupConfig = m_gConfig.Configs["Startup"];

            if(startupConfig == null) // should not happen
                return;

            IConfig economyConfig = m_gConfig.Configs["Economy"];

            // economymodule may be at startup or Economy (legacy)
            string mmodule = startupConfig.GetString("economymodule","");
            if(string.IsNullOrEmpty(mmodule))
            {
                if(economyConfig != null)
                {
                    mmodule = economyConfig.GetString("economymodule", "");
                    if (String.IsNullOrEmpty(mmodule))
                        mmodule = economyConfig.GetString("EconomyModule", "");
                }
            }

            if (!string.IsNullOrEmpty(mmodule) && mmodule != Name)
            {
                // some other money module selected
                m_enabled = false;
                return;
            }

            if(economyConfig == null)
            {
                if (!Path.IsPathRooted(m_balanceStoragePath))
                    m_balanceStoragePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_balanceStoragePath);
                return;
            }

            PriceEnergyUnit = economyConfig.GetInt("PriceEnergyUnit", 0);
            PriceObjectClaim = economyConfig.GetInt("PriceObjectClaim", 0);
            PricePublicObjectDecay = economyConfig.GetInt("PricePublicObjectDecay", 4);
            PricePublicObjectDelete = economyConfig.GetInt("PricePublicObjectDelete", 0);
            PriceParcelClaim = economyConfig.GetInt("PriceParcelClaim", 0);
            PriceParcelClaimFactor = economyConfig.GetFloat("PriceParcelClaimFactor", 1f);
            PriceUpload = economyConfig.GetInt("PriceUpload", 0);
            PriceRentLight = economyConfig.GetInt("PriceRentLight", 0);
            TeleportMinPrice = economyConfig.GetInt("TeleportMinPrice", 0);
            TeleportPriceExponent = economyConfig.GetFloat("TeleportPriceExponent", 2f);
            EnergyEfficiency = economyConfig.GetFloat("EnergyEfficiency", 1);
            PriceObjectRent = economyConfig.GetFloat("PriceObjectRent", 0);
            PriceObjectScaleFactor = economyConfig.GetFloat("PriceObjectScaleFactor", 10);
            PriceParcelRent = economyConfig.GetInt("PriceParcelRent", 0);
            PriceGroupCreate = economyConfig.GetInt("PriceGroupCreate", -1);
            m_sellEnabled = economyConfig.GetBoolean("SellEnabled", true);
            m_initialBalance = Math.Max(0, economyConfig.GetInt("InitialBalance", m_initialBalance));
            m_allowNegativeBalances = economyConfig.GetBoolean("AllowNegativeBalances", false);
            m_balanceStoragePath = economyConfig.GetString("BalanceStorage", m_balanceStoragePath).Trim();
            if (string.IsNullOrWhiteSpace(m_balanceStoragePath))
                m_balanceStoragePath = "Currency/balances.tsv";
            if (!Path.IsPathRooted(m_balanceStoragePath))
                m_balanceStoragePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_balanceStoragePath);
        }

        private void GetClientFunds(IClientAPI client)
        {
            CheckExistAndRefreshFunds(client.AgentId);
        }

        /// <summary>
        /// New Client Event Handler
        /// </summary>
        /// <param name="client"></param>
        private void OnNewClient(IClientAPI client)
        {
            GetClientFunds(client);

            // Subscribe to Money messages
            client.OnEconomyDataRequest += EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest += SendMoneyBalance;
            client.OnRequestPayPrice += requestPayPrice;
            client.OnObjectBuy += ObjectBuy;
            client.OnLogout += ClientLoggedOut;

            SendMoneyBalance(client, client.AgentId, client.SessionId, UUID.Random());
        }

        /// <summary>
        /// Transfer money
        /// </summary>
        /// <param name="Sender"></param>
        /// <param name="Receiver"></param>
        /// <param name="amount"></param>
        /// <returns></returns>
        private bool doMoneyTransfer(UUID Sender, UUID Receiver, int amount, int transactiontype, string description, out string reason)
        {
            return TransferMoney(Sender, Receiver, amount, transactiontype, description, out reason);
        }


        /// <summary>
        /// Sends the the stored money balance to the client
        /// </summary>
        /// <param name="client"></param>
        /// <param name="agentID"></param>
        /// <param name="SessionID"></param>
        /// <param name="TransactionID"></param>
        public void SendMoneyBalance(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
        {
            if (client.AgentId == agentID && client.SessionId == SessionID)
            {
                int returnfunds = 0;

                try
                {
                    returnfunds = GetFundsForAgentID(agentID);
                }
                catch (Exception e)
                {
                    client.SendAlertMessage(e.Message + " ");
                }

                client.SendMoneyBalance(TransactionID, true, Array.Empty<byte>(), returnfunds, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
            }
            else
            {
                client.SendAlertMessage("Unable to send your money balance to you!");
            }
        }

        private SceneObjectPart findPrim(UUID objectID)
        {
            lock (m_scenes)
            {
                foreach (Scene s in m_scenes.Values)
                {
                    SceneObjectPart part = s.GetSceneObjectPart(objectID);
                    if (part != null)
                    {
                        return part;
                    }
                }
            }
            return null;
        }

        private string resolveObjectName(UUID objectID)
        {
            SceneObjectPart part = findPrim(objectID);
            if (part != null)
            {
                return part.Name;
            }
            return String.Empty;
        }

        private string resolveAgentName(UUID agentID)
        {
            // try avatar username surname
            Scene scene = GetRandomScene();
            UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, agentID);
            if (account != null)
            {
                string avatarname = account.FirstName + " " + account.LastName;
                return avatarname;
            }
            else
            {
                m_log.ErrorFormat(
                    "[MONEY]: Could not resolve user {0}",
                    agentID);
            }

            return String.Empty;
        }

        private void BalanceUpdate(UUID senderID, UUID receiverID, bool transactionresult, string description)
        {
            BalanceUpdate(senderID, receiverID, transactionresult, description, 0, 0);
        }

        private void BalanceUpdate(UUID senderID, UUID receiverID, bool transactionresult, string description, int transactionType, int amount)
        {
            SendBalanceUpdateTo(senderID, senderID, receiverID, transactionresult, description, transactionType, amount);
            if (receiverID != senderID)
                SendBalanceUpdateTo(receiverID, senderID, receiverID, transactionresult, description, transactionType, amount);
        }

        private void SendBalanceUpdateTo(UUID agentID, UUID sourceID, UUID destID, bool transactionresult, string description, int transactionType, int amount)
        {
            if (agentID.IsZero())
                return;

            IClientAPI client = LocateClientObject(agentID);
            if (client == null)
                return;

            byte[] message = string.IsNullOrEmpty(description)
                ? Array.Empty<byte>()
                : Utils.StringToBytes(description);
            client.SendMoneyBalance(UUID.Random(), transactionresult, message, GetFundsForAgentID(agentID),
                    transactionType, sourceID, false, destID, false, amount, description ?? String.Empty);
        }

        /// <summary>
        /// XMLRPC handler to send alert message and sound to client
        /// </summary>
        public XmlRpcResponse UserAlert(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse ret = new XmlRpcResponse();
            Hashtable retparam = new Hashtable();
            Hashtable requestData = (Hashtable) request.Params[0];

            UUID agentId;
            UUID soundId;
            UUID regionId;

            UUID.TryParse((string) requestData["agentId"], out agentId);
            UUID.TryParse((string) requestData["soundId"], out soundId);
            UUID.TryParse((string) requestData["regionId"], out regionId);
            string text = (string) requestData["text"];
            string secret = (string) requestData["secret"];

            Scene userScene = GetSceneByUUID(regionId);
            if (userScene != null)
            {
                if (userScene.RegionInfo.regionSecret == secret)
                {

                    IClientAPI client = LocateClientObject(agentId);
                       if (client != null)
                       {

                           if (!soundId.IsZero())
                               client.SendPlayAttachedSound(soundId, UUID.Zero, UUID.Zero, 1.0f, 0);

                           client.SendBlueBoxMessage(UUID.Zero, "", text);

                           retparam.Add("success", true);
                       }
                    else
                    {
                        retparam.Add("success", false);
                    }
                }
                else
                {
                    retparam.Add("success", false);
                }
            }

            ret.Value = retparam;
            return ret;
        }

        # region Standalone box enablers only

        public XmlRpcResponse quote_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // UUID agentId = UUID.Zero;
            int amount = 0;
            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                amount = (int)requestData["currencyBuy"];
            }
            catch{ }

            Hashtable currencyResponse = new Hashtable();
            currencyResponse.Add("estimatedCost", 0);
            //currencyResponse.Add("estimatedLocalCost", " 0 Euros");

            currencyResponse.Add("currencyBuy", amount);

            Hashtable quoteResponse = new Hashtable();
            quoteResponse.Add("success", true);
            quoteResponse.Add("currency", currencyResponse);
            quoteResponse.Add("confirm", "asdfad9fj39ma9fj");

            //quoteResponse.Add("success", false);
            //quoteResponse.Add("errorMessage", "There is currency");
            //quoteResponse.Add("errorURI", "http://opensimulator.org");
            XmlRpcResponse returnval = new XmlRpcResponse();
            returnval.Value = quoteResponse;
            return returnval;
        }

        public XmlRpcResponse buy_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            UUID agentId = UUID.Zero;
            int amount = 0;
            try
            {
                Hashtable requestData = (Hashtable) request.Params[0];
                if (requestData.ContainsKey("agentId"))
                    UUID.TryParse((string)requestData["agentId"], out agentId);
                if (requestData.ContainsKey("currencyBuy"))
                    amount = Convert.ToInt32(requestData["currencyBuy"]);
            }
            catch
            {
            }

            if (agentId.IsNotZero() && amount > 0)
            {
                Credit(agentId, amount);
                BalanceUpdate(UUID.Zero, agentId, true, "Currency purchase", (int)TransactionType.SystemGenerated, amount);
            }

            XmlRpcResponse returnval = new XmlRpcResponse();
            Hashtable returnresp = new Hashtable();
            returnresp.Add("success", true);
            returnval.Value = returnresp;
            return returnval;
        }

        public XmlRpcResponse preflightBuyLandPrep_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse ret = new XmlRpcResponse();
            Hashtable retparam = new Hashtable();
            Hashtable membershiplevels = new Hashtable();
            ArrayList levels = new ArrayList();
            Hashtable level = new Hashtable();
            level.Add("id", "00000000-0000-0000-0000-000000000000");
            level.Add("description", "some level");
            levels.Add(level);
            //membershiplevels.Add("levels",levels);

            Hashtable landuse = new Hashtable();
            landuse.Add("upgrade", false);
            landuse.Add("action", "http://invaliddomaininvalid.com/");

            Hashtable currency = new Hashtable();
            currency.Add("estimatedCost", 0);

            Hashtable membership = new Hashtable();
            membershiplevels.Add("upgrade", false);
            membershiplevels.Add("action", "http://invaliddomaininvalid.com/");
            membershiplevels.Add("levels", membershiplevels);

            retparam.Add("success", true);
            retparam.Add("currency", currency);
            retparam.Add("membership", membership);
            retparam.Add("landuse", landuse);
            retparam.Add("confirm", "asdfajsdkfjasdkfjalsdfjasdf");

            ret.Value = retparam;

            return ret;
        }

        public XmlRpcResponse landBuy_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse ret = new XmlRpcResponse();
            Hashtable retparam = new Hashtable();
            // Hashtable requestData = (Hashtable) request.Params[0];

            // UUID agentId = UUID.Zero;
            // int amount = 0;

            retparam.Add("success", true);
            ret.Value = retparam;

            return ret;
        }

        #endregion

        #region local Fund Management

        /// <summary>
        /// Ensures that the agent accounting data is set up in this instance.
        /// </summary>
        /// <param name="agentID"></param>
        private void CheckExistAndRefreshFunds(UUID agentID)
        {
            if (agentID.IsZero())
                return;

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                if (!m_balances.ContainsKey(agentID))
                {
                    m_balances[agentID] = m_initialBalance;
                    SaveBalancesLocked();
                }
            }
        }

        /// <summary>
        /// Gets the amount of Funds for an agent
        /// </summary>
        /// <param name="AgentID"></param>
        /// <returns></returns>
        private int GetFundsForAgentID(UUID AgentID)
        {
            if (AgentID.IsZero())
                return 0;

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                if (!m_balances.TryGetValue(AgentID, out int returnfunds))
                {
                    returnfunds = m_initialBalance;
                    m_balances[AgentID] = returnfunds;
                    SaveBalancesLocked();
                }

                return returnfunds;
            }
        }

        private bool Debit(UUID agentID, int amount, out string reason)
        {
            reason = String.Empty;
            if (amount <= 0)
                return true;
            if (agentID.IsZero())
            {
                reason = "Invalid money source.";
                return false;
            }

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                if (!m_balances.TryGetValue(agentID, out int balance))
                    balance = m_initialBalance;
                if (!m_allowNegativeBalances && balance < amount)
                {
                    reason = "Insufficient funds.";
                    return false;
                }

                m_balances[agentID] = balance - amount;
                SaveBalancesLocked();
                return true;
            }
        }

        private void Credit(UUID agentID, int amount)
        {
            if (amount <= 0 || agentID.IsZero())
                return;

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                if (!m_balances.TryGetValue(agentID, out int balance))
                    balance = m_initialBalance;
                m_balances[agentID] = balance + amount;
                SaveBalancesLocked();
            }
        }

        private bool TransferMoney(UUID fromUser, UUID toUser, int amount, int transactionType, string text, out string reason)
        {
            reason = String.Empty;
            if (amount <= 0)
            {
                reason = "Amount must be greater than zero.";
                return false;
            }
            if (fromUser.IsZero())
            {
                reason = "Invalid money source.";
                return false;
            }
            if (fromUser == toUser)
                return true;

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                if (!m_balances.TryGetValue(fromUser, out int fromBalance))
                    fromBalance = m_initialBalance;
                if (!m_allowNegativeBalances && fromBalance < amount)
                {
                    reason = "Insufficient funds.";
                    return false;
                }

                if (!m_balances.TryGetValue(toUser, out int toBalance))
                    toBalance = m_initialBalance;

                m_balances[fromUser] = fromBalance - amount;
                if (!toUser.IsZero() && toUser != fromUser)
                    m_balances[toUser] = toBalance + amount;
                SaveBalancesLocked();
                return true;
            }
        }

        private void EnsureBalancesLoaded()
        {
            if (m_balancesLoaded)
                return;

            m_balancesLoaded = true;
            try
            {
                string directory = Path.GetDirectoryName(m_balanceStoragePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                if (!File.Exists(m_balanceStoragePath))
                    return;

                foreach (string rawLine in File.ReadAllLines(m_balanceStoragePath))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 2)
                        continue;
                    if (UUID.TryParse(parts[0], out UUID agentID) && int.TryParse(parts[1], out int balance))
                        m_balances[agentID] = balance;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[MONEY]: Failed loading currency balances from {0}: {1}", m_balanceStoragePath, e.Message);
            }
        }

        private void SaveBalancesLocked()
        {
            try
            {
                string directory = Path.GetDirectoryName(m_balanceStoragePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                List<string> lines = new List<string>();
                lines.Add("# agent_id\tbalance");
                foreach (KeyValuePair<UUID, int> entry in m_balances)
                    lines.Add(entry.Key + "\t" + entry.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

                string tmp = m_balanceStoragePath + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(m_balanceStoragePath))
                    File.Delete(m_balanceStoragePath);
                File.Move(tmp, m_balanceStoragePath);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[MONEY]: Failed saving currency balances to {0}: {1}", m_balanceStoragePath, e.Message);
            }
        }


        #endregion

        #region Utility Helpers

        /// <summary>
        /// Locates a IClientAPI for the client specified
        /// </summary>
        /// <param name="AgentID"></param>
        /// <returns></returns>
        private IClientAPI LocateClientObject(UUID AgentID)
        {
            ScenePresence tPresence;
            lock (m_scenes)
            {
                foreach (Scene _scene in m_scenes.Values)
                {
                    tPresence = _scene.GetScenePresence(AgentID);
                    if (tPresence != null && !tPresence.IsDeleted && !tPresence.IsChildAgent)
                        return tPresence.ControllingClient;
                }
            }
            return null;
        }

        private Scene LocateSceneClientIn(UUID AgentId)
        {
            lock (m_scenes)
            {
                foreach (Scene _scene in m_scenes.Values)
                {
                    ScenePresence tPresence = _scene.GetScenePresence(AgentId);
                    if (tPresence != null && !tPresence.IsDeleted && !tPresence.IsChildAgent)
                        return _scene;
                }
            }
            return null;
        }

        /// <summary>
        /// Utility function Gets a Random scene in the instance.  For when which scene exactly you're doing something with doesn't matter
        /// </summary>
        /// <returns></returns>
        public Scene GetRandomScene()
        {
            lock (m_scenes)
            {
                foreach (Scene rs in m_scenes.Values)
                    return rs;
            }
            return null;
        }

        /// <summary>
        /// Utility function to get a Scene by RegionID in a module
        /// </summary>
        /// <param name="RegionID"></param>
        /// <returns></returns>
        public Scene GetSceneByUUID(UUID RegionID)
        {
            lock (m_scenes)
            {
                foreach (Scene rs in m_scenes.Values)
                {
                    if (rs.RegionInfo.originRegionID == RegionID)
                    {
                        return rs;
                    }
                }
            }
            return null;
        }

        #endregion

        #region event Handlers

        public void requestPayPrice(IClientAPI client, UUID objectID)
        {
            Scene scene = LocateSceneClientIn(client.AgentId);
            if (scene == null)
                return;

            SceneObjectPart task = scene.GetSceneObjectPart(objectID);
            if (task == null)
                return;
            SceneObjectGroup group = task.ParentGroup;
            SceneObjectPart root = group.RootPart;

            client.SendPayPrice(objectID, root.PayPrice);
        }

        /// <summary>
        /// When the client closes the connection we remove their accounting
        /// info from memory to free up resources.
        /// </summary>
        /// <param name="AgentID">UUID of agent</param>
        /// <param name="scene">Scene the agent was connected to.</param>
        /// <see cref="OpenSim.Region.Framework.Scenes.EventManager.ClientClosed"/>
        public void ClientClosed(UUID AgentID, Scene scene)
        {

        }

        /// <summary>
        /// Event called Economy Data Request handler.
        /// </summary>
        /// <param name="agentId"></param>
        public void EconomyDataRequestHandler(IClientAPI user)
        {
            Scene s = (Scene)user.Scene;

            user.SendEconomyData(EnergyEfficiency, s.RegionInfo.ObjectCapacity, ObjectCount, PriceEnergyUnit, PriceGroupCreate,
                                 PriceObjectClaim, PriceObjectRent, PriceObjectScaleFactor, PriceParcelClaim, PriceParcelClaimFactor,
                                 PriceParcelRent, PricePublicObjectDecay, PricePublicObjectDelete, PriceRentLight, PriceUpload,
                                 TeleportMinPrice, TeleportPriceExponent);
        }

        private void ValidateLandBuy(Object osender, EventManager.LandBuyArgs e)
        {
            lock (e)
            {
                if (e.parcelPrice <= 0)
                {
                    e.economyValidated = true;
                    return;
                }

                if (!AmountCovered(e.agentId, e.parcelPrice))
                {
                    IClientAPI client = LocateClientObject(e.agentId);
                    client?.SendAgentAlertMessage("Insufficient funds to buy land.", false);
                    e.economyValidated = false;
                    return;
                }

                if (e.final && e.landValidated && e.amountDebited <= 0)
                {
                    if (Debit(e.agentId, e.parcelPrice, out string reason))
                    {
                        e.amountDebited = e.parcelPrice;
                        e.transactionID = UUID.Random().GetHashCode() & Int32.MaxValue;
                        e.economyValidated = true;
                        BalanceUpdate(e.agentId, e.parcelOwnerID, true, "Land purchase", (int)TransactionType.Purchase, e.parcelPrice);
                    }
                    else
                    {
                        IClientAPI client = LocateClientObject(e.agentId);
                        client?.SendAgentAlertMessage(reason, false);
                        e.economyValidated = false;
                    }
                    return;
                }

                e.economyValidated = true;
            }
        }

        private void processLandBuy(Object osender, EventManager.LandBuyArgs e)
        {
            lock (e)
            {
                if (!e.final || !e.landValidated || !e.economyValidated || e.parcelPrice <= 0 || e.amountDebited > 0)
                    return;

                if (Debit(e.agentId, e.parcelPrice, out string reason))
                {
                    e.amountDebited = e.parcelPrice;
                    e.transactionID = UUID.Random().GetHashCode() & Int32.MaxValue;
                    BalanceUpdate(e.agentId, e.parcelOwnerID, true, "Land purchase", (int)TransactionType.Purchase, e.parcelPrice);
                }
                else
                {
                    e.economyValidated = false;
                    IClientAPI client = LocateClientObject(e.agentId);
                    client?.SendAgentAlertMessage(reason, false);
                    BalanceUpdate(e.agentId, e.parcelOwnerID, false, reason, (int)TransactionType.Purchase, e.parcelPrice);
                }
            }
        }

        /// <summary>
        /// THis method gets called when someone pays someone else as a gift.
        /// </summary>
        /// <param name="osender"></param>
        /// <param name="e"></param>
        private void MoneyTransferAction(Object osender, EventManager.MoneyTransferArgs e)
        {
            UUID payee = e.receiver;
            UUID paidObject = UUID.Zero;
            SceneObjectPart paidPart = findPrim(e.receiver);
            if (paidPart != null)
            {
                paidObject = paidPart.UUID;
                payee = paidPart.OwnerID;
            }

            bool result = TransferMoney(e.sender, payee, e.amount, e.transactiontype, e.description, out string reason);
            string description = string.IsNullOrWhiteSpace(e.description) ? "Money transfer" : e.description;
            if (!result && !string.IsNullOrWhiteSpace(reason))
                description = reason;

            BalanceUpdate(e.sender, payee, result, description, e.transactiontype, e.amount);
            if (result && paidObject.IsNotZero())
                OnObjectPaid?.Invoke(paidObject, e.sender, e.amount);
        }

        /// <summary>
        /// Event Handler for when a root agent becomes a child agent
        /// </summary>
        /// <param name="avatar"></param>
        private void MakeChildAgent(ScenePresence avatar)
        {

        }

        /// <summary>
        /// Event Handler for when the client logs out.
        /// </summary>
        /// <param name="AgentId"></param>
        private void ClientLoggedOut(IClientAPI client)
        {

        }

        /// <summary>
        /// Call this when the client disconnects.
        /// </summary>
        /// <param name="client"></param>
        public void ClientClosed(IClientAPI client)
        {
            ClientClosed(client.AgentId, null);
        }

        /// <summary>
        /// Event Handler for when an Avatar enters one of the parcels in the simulator.
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="localLandID"></param>
        /// <param name="regionID"></param>
        private void AvatarEnteringParcel(ScenePresence avatar, int localLandID, UUID regionID)
        {

            //m_log.Info("[FRIEND]: " + avatar.Name + " status:" + (!avatar.IsChildAgent).ToString());
        }

        public int GetBalance(UUID agentID)
        {
            return GetFundsForAgentID(agentID);
        }

        // Please do not refactor these to be just one method
        // Existing implementations need the distinction
        //
        public bool UploadCovered(UUID agentID, int amount)
        {
            return AmountCovered(agentID, amount);
        }
        public bool AmountCovered(UUID agentID, int amount)
        {
            return m_allowNegativeBalances || amount <= 0 || GetFundsForAgentID(agentID) >= amount;
        }

        #endregion

        public void ObjectBuy(IClientAPI remoteClient, UUID agentID,
                UUID sessionID, UUID groupID, UUID categoryID,
                uint localID, byte saleType, int salePrice)
        {
            if (!m_sellEnabled)
            {
                remoteClient.SendBlueBoxMessage(UUID.Zero, "", "Buying is not implemented in this version");
                return;
            }

            Scene s = LocateSceneClientIn(remoteClient.AgentId);
            if (s == null)
                return;

            // Implmenting base sale data checking here so the default OpenSimulator implementation isn't useless
            // combined with other implementations.  We're actually validating that the client is sending the data
            // that it should.   In theory, the client should already know what to send here because it'll see it when it
            // gets the object data.   If the data sent by the client doesn't match the object, the viewer probably has an
            // old idea of what the object properties are.   Viewer developer Hazim informed us that the base module
            // didn't check the client sent data against the object do any.   Since the base modules are the
            // 'crowning glory' examples of good practice..

            // Validate that the object exists in the scene the user is in
            SceneObjectPart part = s.GetSceneObjectPart(localID);
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
            {
                remoteClient.SendAgentAlertMessage("Unable to buy now. The object was not found.", false);
                return;
            }
            if(!part.IsRoot) // silent ignore non root parts
                return;

            if (part.ObjectSaleType == (byte)SaleType.Not)
            {
                string e = string.Format("Object {0} is not for sale", part.Name);
                remoteClient.SendAgentAlertMessage(e, false);
                return;
            }

            // Validate that the client sent the price that the object is being sold for
            if (part.SalePrice != salePrice)
            {
                string e = string.Format("Object {0} price does not match selected price", part.Name);
                remoteClient.SendAgentAlertMessage(e, false);
                return;
            }

            // Validate that the client sent the proper sale type the object has set
            if (part.ObjectSaleType != saleType)
            {
                string e = string.Format("Object {0} sell type does not match selected type", part.Name);
                remoteClient.SendAgentAlertMessage(e, false);
                return;
            }

            IBuySellModule module = s.RequestModuleInterface<IBuySellModule>();
            if (module == null)
                return;

            UUID sellerID = part.OwnerID;
            if (salePrice > 0 && !AmountCovered(remoteClient.AgentId, salePrice))
            {
                remoteClient.SendAgentAlertMessage("Insufficient funds.", false);
                BalanceUpdate(remoteClient.AgentId, sellerID, false, "Insufficient funds.", (int)TransactionType.Purchase, salePrice);
                return;
            }

            if (module.BuyObject(remoteClient, categoryID, localID, saleType, salePrice) && salePrice > 0)
            {
                bool result = TransferMoney(remoteClient.AgentId, sellerID, salePrice, (int)TransactionType.Purchase, "Object purchase", out string reason);
                BalanceUpdate(remoteClient.AgentId, sellerID, result, result ? "Object purchase" : reason, (int)TransactionType.Purchase, salePrice);
            }
        }

        public void MoveMoney(UUID fromUser, UUID toUser, int amount, string text)
        {
            MoveMoney(fromUser, toUser, amount, (MoneyTransactionType)0, text);
        }

        public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
        {
            bool result = TransferMoney(fromUser, toUser, amount, (int)type, text, out string reason);
            BalanceUpdate(fromUser, toUser, result, result ? text : reason, (int)type, amount);
            return result;
        }
    }

    public enum TransactionType : int
    {
        SystemGenerated = 0,
        RegionMoneyRequest = 1,
        Gift = 2,
        Purchase = 3
    }
}
