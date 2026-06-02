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
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using Mono.Addins;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
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
        private string m_transactionLogPath = "Currency/transactions.tsv";
        private bool m_balancesLoaded;
        private bool m_allowNegativeBalances;
        private bool m_auditEnabled = true;
        private bool m_consoleCommandsRegistered;
        private int m_initialBalance = 1000;
        private long m_transactionSequence;

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
            bool result = Debit(agentID, amount, out string reason, (int)type, description);
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
            bool result = Debit(agentID, amount, out string reason, 0, description);
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
            if (!m_enabled || m_consoleCommandsRegistered)
                return;

            m_consoleCommandsRegistered = true;
            MainConsole.Instance.Commands.AddCommand("Money", false, "money show",
                "money show",
                "Show local currency ledger status.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money list",
                "money list [limit]",
                "List local currency accounts and balances.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money balance",
                "money balance <avatar uuid|first last>",
                "Show an avatar's local currency balance.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money set",
                "money set <avatar uuid|first last> <amount>",
                "Set an avatar's local currency balance.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money give",
                "money give <avatar uuid|first last> <amount>",
                "Credit local currency to an avatar.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money take",
                "money take <avatar uuid|first last> <amount>",
                "Debit local currency from an avatar.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money transfer",
                "money transfer <from uuid|first last> to <to uuid|first last> <amount>",
                "Transfer local currency between avatars.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money export",
                "money export [path]",
                "Export the local currency ledger TSV.",
                HandleMoneyCommand);
            MainConsole.Instance.Commands.AddCommand("Money", false, "money import",
                "money import <path>",
                "Import a local currency ledger TSV, backing up the current ledger first.",
                HandleMoneyCommand);
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
                NormalizeCurrencyPaths();
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
            m_auditEnabled = economyConfig.GetBoolean("AuditEnabled", true);
            m_balanceStoragePath = economyConfig.GetString("BalanceStorage", m_balanceStoragePath).Trim();
            if (string.IsNullOrWhiteSpace(m_balanceStoragePath))
                m_balanceStoragePath = "Currency/balances.tsv";
            m_transactionLogPath = economyConfig.GetString("TransactionLog", m_transactionLogPath).Trim();
            if (string.IsNullOrWhiteSpace(m_transactionLogPath))
                m_transactionLogPath = "Currency/transactions.tsv";
            NormalizeCurrencyPaths();
        }

        private void NormalizeCurrencyPaths()
        {
            if (!Path.IsPathRooted(m_balanceStoragePath))
                m_balanceStoragePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_balanceStoragePath);
            if (!Path.IsPathRooted(m_transactionLogPath))
                m_transactionLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_transactionLogPath);
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
            if (scene == null || scene.UserAccountService == null)
                return String.Empty;

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
                Credit(agentId, amount, (int)TransactionType.SystemGenerated, "Currency purchase");
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
                    RecordTransactionLocked("create", UUID.Zero, agentID, m_initialBalance, (int)TransactionType.SystemGenerated, true, "Initial balance", 0, m_initialBalance);
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
                    RecordTransactionLocked("create", UUID.Zero, AgentID, m_initialBalance, (int)TransactionType.SystemGenerated, true, "Initial balance", 0, returnfunds);
                }

                return returnfunds;
            }
        }

        private bool Debit(UUID agentID, int amount, out string reason)
        {
            return Debit(agentID, amount, out reason, 0, "Debit");
        }

        private bool Debit(UUID agentID, int amount, out string reason, int transactionType, string description)
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
                    RecordTransactionLocked("debit", agentID, UUID.Zero, amount, transactionType, false, reason, balance, 0);
                    return false;
                }

                int newBalance = balance - amount;
                m_balances[agentID] = newBalance;
                SaveBalancesLocked();
                RecordTransactionLocked("debit", agentID, UUID.Zero, amount, transactionType, true, description, newBalance, 0);
                return true;
            }
        }

        private void Credit(UUID agentID, int amount)
        {
            Credit(agentID, amount, 0, "Credit");
        }

        private void Credit(UUID agentID, int amount, int transactionType, string description)
        {
            if (amount <= 0 || agentID.IsZero())
                return;

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                if (!m_balances.TryGetValue(agentID, out int balance))
                    balance = m_initialBalance;
                int newBalance = balance + amount;
                m_balances[agentID] = newBalance;
                SaveBalancesLocked();
                RecordTransactionLocked("credit", UUID.Zero, agentID, amount, transactionType, true, description, 0, newBalance);
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
                int toBalance = 0;
                if (!toUser.IsZero() && !m_balances.TryGetValue(toUser, out toBalance))
                    toBalance = m_initialBalance;
                if (!m_allowNegativeBalances && fromBalance < amount)
                {
                    reason = "Insufficient funds.";
                    RecordTransactionLocked("transfer", fromUser, toUser, amount, transactionType, false, reason, fromBalance, toBalance);
                    return false;
                }

                int newFromBalance = fromBalance - amount;
                int newToBalance = toBalance;
                m_balances[fromUser] = newFromBalance;
                if (!toUser.IsZero() && toUser != fromUser)
                {
                    newToBalance = toBalance + amount;
                    m_balances[toUser] = newToBalance;
                }
                SaveBalancesLocked();
                RecordTransactionLocked("transfer", fromUser, toUser, amount, transactionType, true, text, newFromBalance, newToBalance);
                return true;
            }
        }

        public Dictionary<string, string> GetCurrencyStats()
        {
            Dictionary<string, string> stats = new Dictionary<string, string>();
            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();

                long total = 0;
                int minimum = 0;
                int maximum = 0;
                bool first = true;

                foreach (int balance in m_balances.Values)
                {
                    total += balance;
                    if (first)
                    {
                        minimum = balance;
                        maximum = balance;
                        first = false;
                    }
                    else
                    {
                        if (balance < minimum)
                            minimum = balance;
                        if (balance > maximum)
                            maximum = balance;
                    }
                }

                stats["Accounts"] = m_balances.Count.ToString(CultureInfo.InvariantCulture);
                stats["Total balance"] = total.ToString(CultureInfo.InvariantCulture);
                stats["Minimum balance"] = first ? "0" : minimum.ToString(CultureInfo.InvariantCulture);
                stats["Maximum balance"] = first ? "0" : maximum.ToString(CultureInfo.InvariantCulture);
                stats["Initial balance"] = m_initialBalance.ToString(CultureInfo.InvariantCulture);
                stats["Negative balances"] = m_allowNegativeBalances ? "allowed" : "blocked";
                stats["Audit log"] = m_auditEnabled ? "enabled" : "disabled";
                stats["Ledger"] = m_balanceStoragePath;
            }

            return stats;
        }

        public bool WebBuyCurrency(UUID agentID, int amount, out string reason)
        {
            reason = String.Empty;
            if (agentID.IsZero())
            {
                reason = "Invalid avatar.";
                return false;
            }
            if (amount <= 0)
            {
                reason = "Amount must be greater than zero.";
                return false;
            }

            Credit(agentID, amount, (int)TransactionType.SystemGenerated, "RegionWeb token purchase");
            BalanceUpdate(UUID.Zero, agentID, true, "RegionWeb token purchase", (int)TransactionType.SystemGenerated, amount);
            return true;
        }

        public bool WebTransfer(UUID fromUser, UUID toUser, int amount, string description, out string reason)
        {
            reason = String.Empty;
            if (fromUser.IsZero() || toUser.IsZero())
            {
                reason = "Invalid avatar.";
                return false;
            }
            if (fromUser == toUser)
            {
                reason = "Cannot transfer to the same avatar.";
                return false;
            }
            if (amount <= 0)
            {
                reason = "Amount must be greater than zero.";
                return false;
            }

            string text = string.IsNullOrWhiteSpace(description) ? "RegionWeb transfer" : description.Trim();
            bool result = TransferMoney(fromUser, toUser, amount, (int)TransactionType.Gift, text, out reason);
            BalanceUpdate(fromUser, toUser, result, result ? text : reason, (int)TransactionType.Gift, amount);
            return result;
        }

        public List<Dictionary<string, string>> GetCurrencyStatement(UUID agentID, int limit)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            if (agentID.IsZero())
                return rows;
            if (limit <= 0)
                limit = 25;

            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();

                if (!File.Exists(m_transactionLogPath))
                    return rows;

                string agentText = agentID.ToString();
                string[] lines = File.ReadAllLines(m_transactionLogPath);
                for (int i = lines.Length - 1; i >= 0 && rows.Count < limit; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 11)
                        continue;
                    if (!parts[3].Equals(agentText, StringComparison.OrdinalIgnoreCase)
                            && !parts[4].Equals(agentText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    row["utc"] = parts[0];
                    row["sequence"] = parts[1];
                    row["action"] = parts[2];
                    row["source"] = parts[3];
                    row["destination"] = parts[4];
                    row["amount"] = parts[5];
                    row["transaction_type"] = parts[6];
                    row["success"] = parts[7];
                    row["source_balance"] = parts[8];
                    row["destination_balance"] = parts[9];
                    row["description"] = parts[10];
                    row["direction"] = parts[4].Equals(agentText, StringComparison.OrdinalIgnoreCase) ? "credit" : "debit";
                    row["balance"] = row["direction"] == "credit" ? parts[9] : parts[8];
                    rows.Add(row);
                }
            }

            return rows;
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

                Dictionary<UUID, int> loaded = LoadBalanceFile(m_balanceStoragePath);
                m_balances.Clear();
                foreach (KeyValuePair<UUID, int> entry in loaded)
                    m_balances[entry.Key] = entry.Value;
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
                    lines.Add(entry.Key + "\t" + entry.Value.ToString(CultureInfo.InvariantCulture));

                string tmp = m_balanceStoragePath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), Encoding.UTF8);
                if (File.Exists(m_balanceStoragePath))
                    File.Delete(m_balanceStoragePath);
                File.Move(tmp, m_balanceStoragePath);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[MONEY]: Failed saving currency balances to {0}: {1}", m_balanceStoragePath, e.Message);
            }
        }

        private Dictionary<UUID, int> LoadBalanceFile(string path)
        {
            Dictionary<UUID, int> loaded = new Dictionary<UUID, int>();
            if (!File.Exists(path))
                return loaded;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                string[] parts = line.Split('\t');
                if (parts.Length < 2)
                    continue;
                if (UUID.TryParse(parts[0], out UUID agentID) && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int balance))
                    loaded[agentID] = balance;
            }

            return loaded;
        }

        private void RecordTransactionLocked(string action, UUID source, UUID destination, int amount, int transactionType, bool success, string description, int sourceBalance, int destinationBalance)
        {
            if (!m_auditEnabled)
                return;

            try
            {
                string directory = Path.GetDirectoryName(m_transactionLogPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                bool writeHeader = !File.Exists(m_transactionLogPath) || File.GetLength(m_transactionLogPath) == 0;
                StringBuilder line = new StringBuilder();
                if (writeHeader)
                    line.Append("# utc\tsequence\taction\tsource\tdestination\tamount\ttransaction_type\tsuccess\tsource_balance\tdestination_balance\tdescription\n");

                long sequence = ++m_transactionSequence;
                line.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(sequence.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(EscapeTsv(action)).Append('\t')
                    .Append(source).Append('\t')
                    .Append(destination).Append('\t')
                    .Append(amount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(transactionType.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(success ? "1" : "0").Append('\t')
                    .Append(sourceBalance.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(destinationBalance.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(EscapeTsv(description)).Append('\n');

                File.AppendAllText(m_transactionLogPath, line.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[MONEY]: Failed writing transaction audit log {0}: {1}", m_transactionLogPath, e.Message);
            }
        }

        private static string EscapeTsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private void HandleMoneyCommand(string module, string[] cmd)
        {
            if (cmd.Length < 2)
            {
                ShowMoneyHelp();
                return;
            }

            string verb = cmd[1].ToLowerInvariant();
            switch (verb)
            {
                case "show":
                    HandleMoneyShow();
                    return;
                case "list":
                    HandleMoneyList(cmd);
                    return;
                case "balance":
                    HandleMoneyBalance(cmd);
                    return;
                case "set":
                    HandleMoneySet(cmd);
                    return;
                case "give":
                    HandleMoneyGive(cmd, true);
                    return;
                case "take":
                    HandleMoneyGive(cmd, false);
                    return;
                case "transfer":
                    HandleMoneyTransfer(cmd);
                    return;
                case "export":
                    HandleMoneyExport(cmd);
                    return;
                case "import":
                    HandleMoneyImport(cmd);
                    return;
                default:
                    ShowMoneyHelp();
                    return;
            }
        }

        private void ShowMoneyHelp()
        {
            MainConsole.Instance.Output("[MONEY]: money show | money list [limit] | money balance <avatar> | money set <avatar> <amount> | money give <avatar> <amount> | money take <avatar> <amount> | money transfer <from> to <to> <amount> | money export [path] | money import <path>");
        }

        private void HandleMoneyShow()
        {
            Dictionary<string, string> stats = GetCurrencyStats();
            MainConsole.Instance.Output("[MONEY]: Local currency ledger");
            foreach (KeyValuePair<string, string> entry in stats)
                MainConsole.Instance.OutputFormat("[MONEY]: {0}: {1}", entry.Key, entry.Value);
            MainConsole.Instance.OutputFormat("[MONEY]: Audit path: {0}", m_transactionLogPath);
        }

        private void HandleMoneyList(string[] cmd)
        {
            int limit = 20;
            if (cmd.Length >= 3)
                Int32.TryParse(cmd[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
            if (limit <= 0)
                limit = 20;

            List<KeyValuePair<UUID, int>> entries;
            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                entries = new List<KeyValuePair<UUID, int>>(m_balances);
            }

            entries.Sort((a, b) => b.Value.CompareTo(a.Value));
            MainConsole.Instance.OutputFormat("[MONEY]: Showing {0} of {1} accounts", Math.Min(limit, entries.Count), entries.Count);
            for (int i = 0; i < entries.Count && i < limit; i++)
                MainConsole.Instance.OutputFormat("[MONEY]: {0} {1}", entries[i].Key, entries[i].Value);
        }

        private void HandleMoneyBalance(string[] cmd)
        {
            if (cmd.Length < 3)
            {
                MainConsole.Instance.Output("[MONEY]: Usage: money balance <avatar uuid|first last>");
                return;
            }

            string agentText = JoinArgs(cmd, 2, cmd.Length);
            if (!TryResolveAgent(agentText, out UUID agentID, out string displayName))
                return;

            MainConsole.Instance.OutputFormat("[MONEY]: {0} ({1}) balance: {2}", displayName, agentID, GetFundsForAgentID(agentID));
        }

        private void HandleMoneySet(string[] cmd)
        {
            if (!TryParseAgentAndAmount(cmd, 2, out UUID agentID, out string displayName, out int amount))
            {
                MainConsole.Instance.Output("[MONEY]: Usage: money set <avatar uuid|first last> <amount>");
                return;
            }
            if (amount < 0 && !m_allowNegativeBalances)
            {
                MainConsole.Instance.Output("[MONEY]: Negative balances are disabled.");
                return;
            }

            int newBalance;
            lock (m_balanceLock)
            {
                EnsureBalancesLoaded();
                m_balances[agentID] = amount;
                SaveBalancesLocked();
                newBalance = amount;
                RecordTransactionLocked("set", UUID.Zero, agentID, amount, (int)TransactionType.SystemGenerated, true, "Console balance set", 0, newBalance);
            }

            SendBalanceUpdateTo(agentID, UUID.Zero, agentID, true, "Console balance set", (int)TransactionType.SystemGenerated, amount);
            MainConsole.Instance.OutputFormat("[MONEY]: Set {0} ({1}) balance to {2}", displayName, agentID, newBalance);
        }

        private void HandleMoneyGive(string[] cmd, bool credit)
        {
            if (!TryParseAgentAndAmount(cmd, 2, out UUID agentID, out string displayName, out int amount) || amount <= 0)
            {
                MainConsole.Instance.Output(credit ? "[MONEY]: Usage: money give <avatar uuid|first last> <amount>" : "[MONEY]: Usage: money take <avatar uuid|first last> <amount>");
                return;
            }

            if (credit)
            {
                Credit(agentID, amount, (int)TransactionType.SystemGenerated, "Console credit");
                SendBalanceUpdateTo(agentID, UUID.Zero, agentID, true, "Console credit", (int)TransactionType.SystemGenerated, amount);
                MainConsole.Instance.OutputFormat("[MONEY]: Credited {0} to {1} ({2}); balance {3}", amount, displayName, agentID, GetFundsForAgentID(agentID));
            }
            else
            {
                bool result = Debit(agentID, amount, out string reason, (int)TransactionType.SystemGenerated, "Console debit");
                SendBalanceUpdateTo(agentID, agentID, UUID.Zero, result, result ? "Console debit" : reason, (int)TransactionType.SystemGenerated, amount);
                MainConsole.Instance.OutputFormat(result
                    ? "[MONEY]: Debited {0} from {1} ({2}); balance {3}"
                    : "[MONEY]: Could not debit {0} from {1} ({2}): {3}",
                    amount, displayName, agentID, result ? GetFundsForAgentID(agentID).ToString(CultureInfo.InvariantCulture) : reason);
            }
        }

        private void HandleMoneyTransfer(string[] cmd)
        {
            if (cmd.Length < 6)
            {
                MainConsole.Instance.Output("[MONEY]: Usage: money transfer <from uuid|first last> to <to uuid|first last> <amount>");
                return;
            }

            if (!Int32.TryParse(cmd[cmd.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
            {
                MainConsole.Instance.Output("[MONEY]: Transfer amount must be greater than zero.");
                return;
            }

            int toIndex = -1;
            for (int i = 2; i < cmd.Length - 1; i++)
            {
                if (cmd[i].Equals("to", StringComparison.OrdinalIgnoreCase))
                {
                    toIndex = i;
                    break;
                }
            }

            if (toIndex <= 2 || toIndex >= cmd.Length - 2)
            {
                MainConsole.Instance.Output("[MONEY]: Usage: money transfer <from uuid|first last> to <to uuid|first last> <amount>");
                return;
            }

            if (!TryResolveAgent(JoinArgs(cmd, 2, toIndex), out UUID fromID, out string fromName))
                return;
            if (!TryResolveAgent(JoinArgs(cmd, toIndex + 1, cmd.Length - 1), out UUID toID, out string toName))
                return;

            bool result = TransferMoney(fromID, toID, amount, (int)TransactionType.SystemGenerated, "Console transfer", out string reason);
            BalanceUpdate(fromID, toID, result, result ? "Console transfer" : reason, (int)TransactionType.SystemGenerated, amount);
            MainConsole.Instance.OutputFormat(result
                ? "[MONEY]: Transferred {0} from {1} ({2}) to {3} ({4})"
                : "[MONEY]: Transfer of {0} failed from {1} ({2}) to {3} ({4}): {5}",
                amount, fromName, fromID, toName, toID, reason);
        }

        private void HandleMoneyExport(string[] cmd)
        {
            string path = cmd.Length >= 3 ? JoinArgs(cmd, 2, cmd.Length) : m_balanceStoragePath + ".export";
            try
            {
                lock (m_balanceLock)
                {
                    EnsureBalancesLoaded();
                    SaveBalancesLocked();
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.Copy(m_balanceStoragePath, path, true);
                MainConsole.Instance.OutputFormat("[MONEY]: Exported ledger to {0}", path);
            }
            catch (Exception e)
            {
                MainConsole.Instance.OutputFormat("[MONEY]: Export failed: {0}", e.Message);
            }
        }

        private void HandleMoneyImport(string[] cmd)
        {
            if (cmd.Length < 3)
            {
                MainConsole.Instance.Output("[MONEY]: Usage: money import <path>");
                return;
            }

            string path = JoinArgs(cmd, 2, cmd.Length);
            try
            {
                if (!File.Exists(path))
                {
                    MainConsole.Instance.OutputFormat("[MONEY]: Import file not found: {0}", path);
                    return;
                }

                Dictionary<UUID, int> imported = LoadBalanceFile(path);
                string backup = m_balanceStoragePath + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

                lock (m_balanceLock)
                {
                    EnsureBalancesLoaded();
                    if (File.Exists(m_balanceStoragePath))
                        File.Copy(m_balanceStoragePath, backup, true);
                    m_balances.Clear();
                    foreach (KeyValuePair<UUID, int> entry in imported)
                        m_balances[entry.Key] = entry.Value;
                    SaveBalancesLocked();
                    RecordTransactionLocked("import", UUID.Zero, UUID.Zero, imported.Count, (int)TransactionType.SystemGenerated, true, "Console ledger import from " + path, 0, 0);
                }

                MainConsole.Instance.OutputFormat("[MONEY]: Imported {0} balances from {1}", imported.Count, path);
                MainConsole.Instance.OutputFormat("[MONEY]: Previous ledger backup: {0}", backup);
            }
            catch (Exception e)
            {
                MainConsole.Instance.OutputFormat("[MONEY]: Import failed: {0}", e.Message);
            }
        }

        private bool TryParseAgentAndAmount(string[] cmd, int start, out UUID agentID, out string displayName, out int amount)
        {
            agentID = UUID.Zero;
            displayName = String.Empty;
            amount = 0;
            if (cmd.Length <= start + 1)
                return false;

            if (!Int32.TryParse(cmd[cmd.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
                return false;

            return TryResolveAgent(JoinArgs(cmd, start, cmd.Length - 1), out agentID, out displayName);
        }

        private bool TryResolveAgent(string value, out UUID agentID, out string displayName)
        {
            agentID = UUID.Zero;
            displayName = value;
            value = (value ?? String.Empty).Trim().Trim('"');
            if (UUID.TryParse(value, out agentID))
            {
                displayName = resolveAgentName(agentID);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = agentID.ToString();
                return true;
            }

            string[] parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                MainConsole.Instance.Output("[MONEY]: Avatar must be a UUID or a first and last name.");
                return false;
            }

            Scene scene = GetRandomScene();
            if (scene == null || scene.UserAccountService == null)
            {
                MainConsole.Instance.Output("[MONEY]: No scene/user account service is available.");
                return false;
            }

            string firstName = parts[0];
            string lastName = String.Join(" ", parts, 1, parts.Length - 1);
            UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, firstName, lastName);
            if (account == null)
            {
                MainConsole.Instance.OutputFormat("[MONEY]: Avatar not found: {0}", value);
                return false;
            }

            agentID = account.PrincipalID;
            displayName = account.Name;
            return true;
        }

        private static string JoinArgs(string[] cmd, int start, int endExclusive)
        {
            if (endExclusive <= start)
                return String.Empty;

            StringBuilder builder = new StringBuilder();
            for (int i = start; i < endExclusive; i++)
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(cmd[i]);
            }
            return builder.ToString();
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
                    if (TransferMoney(e.agentId, e.parcelOwnerID, e.parcelPrice, (int)TransactionType.Purchase, "Land purchase", out string reason))
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

                if (TransferMoney(e.agentId, e.parcelOwnerID, e.parcelPrice, (int)TransactionType.Purchase, "Land purchase", out string reason))
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
            if (salePrice <= 0)
            {
                module.BuyObject(remoteClient, categoryID, localID, saleType, salePrice);
                return;
            }

            if (!TransferMoney(remoteClient.AgentId, sellerID, salePrice, (int)TransactionType.Purchase, "Object purchase", out string reason))
            {
                remoteClient.SendAgentAlertMessage(reason, false);
                BalanceUpdate(remoteClient.AgentId, sellerID, false, reason, (int)TransactionType.Purchase, salePrice);
                return;
            }

            bool buySucceeded = false;
            try
            {
                buySucceeded = module.BuyObject(remoteClient, categoryID, localID, saleType, salePrice);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[MONEY]: Object purchase failed after payment reserve: {0}", e.Message);
            }

            if (buySucceeded)
            {
                BalanceUpdate(remoteClient.AgentId, sellerID, true, "Object purchase", (int)TransactionType.Purchase, salePrice);
                return;
            }

            bool refunded = false;
            if (sellerID.IsNotZero())
                refunded = TransferMoney(sellerID, remoteClient.AgentId, salePrice, (int)TransactionType.Purchase, "Object purchase refund", out string refundReason);
            else
            {
                Credit(remoteClient.AgentId, salePrice, (int)TransactionType.Purchase, "Object purchase refund");
                refunded = true;
            }

            string message = refunded ? "Object purchase failed; funds refunded." : "Object purchase failed; refund failed.";
            remoteClient.SendAgentAlertMessage(message, false);
            BalanceUpdate(remoteClient.AgentId, sellerID, false, message, (int)TransactionType.Purchase, salePrice);
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
