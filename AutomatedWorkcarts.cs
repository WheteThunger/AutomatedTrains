﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using static BaseEntity;
using static TrainCar;
using static TrainEngine;
using static TrainTrackSpline;

namespace Oxide.Plugins
{
    [Info("Automated Workcarts", "WhiteThunder", "0.34.1")]
    [Description("Automates workcarts with NPC conductors.")]
    internal class AutomatedWorkcarts : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin CargoTrainEvent;

        private const string PermissionToggle = "automatedworkcarts.toggle";
        private const string PermissionManageTriggers = "automatedworkcarts.managetriggers";

        private const string ShopkeeperPrefab = "assets/prefabs/npc/bandit/shopkeepers/bandit_shopkeeper.prefab";
        private const string GenericMapMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string VendingMapMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string BradleyExplosionEffectPrefab = "assets/prefabs/npc/m2bradley/effects/bradley_explosion.prefab";

        private static readonly FieldInfo TrainCouplingIsValidField = typeof(TrainCoupling).GetField("isValid", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(TrainCoupling).GetField("isValid", BindingFlags.Public | BindingFlags.Instance);

        private readonly object False = false;
        private static readonly Regex IdRegex = new("\\$id", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly BasePlayer[] _playerQueryResults = new BasePlayer[64];

        private Configuration _config;
        private StoredPluginData _data;
        private StoredTunnelData _tunnelData;
        private StoredMapData _mapData;

        private readonly SpawnedTrainCarTracker _spawnedTrainCarTracker = new();
        private readonly TriggerManager _triggerManager;
        private readonly TrainManager _trainManager;

        private Coroutine _startupCoroutine;

        public AutomatedWorkcarts()
        {
            _trainManager = new TrainManager(this, _spawnedTrainCarTracker);
            _triggerManager = new TriggerManager(this, _trainManager);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _data = StoredPluginData.Load();
            _tunnelData = StoredTunnelData.Load();

            permission.RegisterPermission(PermissionToggle, this);
            permission.RegisterPermission(PermissionManageTriggers, this);

            if (!_config.GenericMapMarker.Enabled)
            {
                Unsubscribe(nameof(OnPlayerConnected));
            }
        }

        private void OnServerInitialized()
        {
            _mapData = StoredMapData.Load();
            _startupCoroutine = ServerMgr.Instance.StartCoroutine(DoStartupRoutine());
        }

        private void Unload()
        {
            if (_startupCoroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(_startupCoroutine);
            }

            OnServerSave();
            _triggerManager.DestroyAll();
            _trainManager.Unload();
        }

        private void OnServerSave()
        {
            if (_trainManager.UpdateTrainEngineData())
            {
                _data.Save();
            }
            else
            {
                _data.SaveIfDirty();
            }
        }

        private void OnNewSave()
        {
            _data = StoredPluginData.Clear();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player.IsReceivingSnapshot)
            {
                timer.Once(1f, () => OnPlayerConnected(player));
                return;
            }

            _trainManager.ResendAllGenericMarkers();
        }

        private object OnTrainCarUncouple(TrainCar trainCar, BasePlayer player)
        {
            // Disallow uncoupling train cars from automated trains.
            return _trainManager.HasTrainController(trainCar)
                ? False
                : null;
        }

        #endregion

        #region Commands

        [Command("aw.toggle")]
        private void CommandAutomateTrain(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionToggle))
                return;

            var basePlayer = player.Object as BasePlayer;

            var trainCar = GetTrainCarWhereAiming(basePlayer);
            if (trainCar == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoWorkcartFound);
                return;
            }

            var trainController = _trainManager.GetTrainController(trainCar);
            if (trainController == null)
            {
                var leadTrainEngine = GetLeadTrainEngine(trainCar);
                if (leadTrainEngine == null)
                {
                    ReplyToPlayer(player, Lang.ErrorNoWorkcart);
                    return;
                }

                if (IsTrainOwned(trainCar))
                {
                    ReplyToPlayer(player, Lang.ErrorWorkcartOwned);
                    return;
                }

                if (!_trainManager.CanHaveMoreConductors())
                {
                    ReplyToPlayer(player, Lang.ErrorMaxConductors, _trainManager.CountedConductors, _config.MaxConductors);
                    return;
                }

                TrainEngineData trainEngineData = null;

                if (args.Length > 0)
                {
                    var routeName = GetRouteNameFromArg(args[0], requirePrefix: false);
                    if (!string.IsNullOrWhiteSpace(routeName))
                    {
                        trainEngineData = new TrainEngineData { Route = routeName };
                    }
                }

                if (_trainManager.TryCreateTrainController(leadTrainEngine, trainEngineData: trainEngineData))
                {
                    var baseMessage = trainEngineData != null
                        ? GetMessage(player, Lang.ToggleOnWithRouteSuccess, trainEngineData.Route)
                        : GetMessage(player, Lang.ToggleOnSuccess);

                    player.Reply(baseMessage + " " + GetConductorCountMessage(player));

                    if (player.HasPermission(PermissionManageTriggers))
                    {
                        if (trainEngineData?.Route != null)
                        {
                            _triggerManager.SetPlayerDisplayedRoute(basePlayer, trainEngineData.Route);
                        }

                        _triggerManager.ShowAllRepeatedly(basePlayer);
                    }
                }
                else
                {
                    ReplyToPlayer(player, Lang.ErrorAutomateBlocked);
                }
            }
            else
            {
                _trainManager.KillTrainController(trainCar);
                player.Reply(GetMessage(player, Lang.ToggleOffSuccess) + " " + GetConductorCountMessage(player));
            }
        }

        [Command("aw.resetall")]
        private void CommandResetTrains(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer
                && !VerifyPermission(player, PermissionToggle))
                return;

            var trainCount = _trainManager.ResetAll();
            ReplyToPlayer(player, Lang.ResetAllSuccess, trainCount);
        }

        [Command("aw.addtrigger", "awt.add")]
        private void CommandAddTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            if (!_config.EnableMapTriggers)
            {
                ReplyToPlayer(player, Lang.ErrorMapTriggersDisabled);
                return;
            }

            if (!VerifyAimingAtTrackPosition(player, out var trackPosition))
                return;

            var triggerData = new TriggerData() { Position = trackPosition };
            AddTriggerShared(player, cmd, args, triggerData);
        }

        [Command("aw.addtunneltrigger", "awt.addt")]
        private void CommandAddTunnelTrigger(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers))
                return;

            if (!VerifyAimingAtTrackPosition(player, out var trackPosition)
                || !VerifySupportedNearbyTrainTunnel(player, trackPosition, out var dungeonCellWrapper))
                return;

            if (!_config.IsTunnelTypeEnabled(dungeonCellWrapper.TunnelType))
            {
                ReplyToPlayer(player, Lang.ErrorTunnelTypeDisabled, dungeonCellWrapper.TunnelType);
                return;
            }

            var triggerData = new TriggerData
            {
                TunnelType = dungeonCellWrapper.TunnelType.ToString(),
                Position = dungeonCellWrapper.InverseTransformPoint(trackPosition),
            };

            AddTriggerShared(player, cmd, args, triggerData, dungeonCellWrapper);
        }

        private void AddTriggerShared(IPlayer player, string cmd, string[] args, TriggerData triggerData, DungeonCellWrapper dungeonCellWrapper = null)
        {
            foreach (var arg in args)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, triggerData, Lang.AddTriggerSyntax))
                    return;
            }

            if (!triggerData.IsSpawner
                && !triggerData.AddConductor
                && !triggerData.Destroy
                && triggerData.GetTrackSelectionInstruction() == null
                && triggerData.GetSpeedInstruction() == null
                && triggerData.GetDirectionInstruction() == null)
            {
                triggerData.Speed = EngineSpeeds.Zero.ToString();
            }

            var basePlayer = player.Object as BasePlayer;

            if (triggerData.IsSpawner)
            {
                var rotation = Quaternion.Euler(basePlayer.viewAngles);
                if (dungeonCellWrapper != null)
                {
                    rotation *= Quaternion.Inverse(dungeonCellWrapper.Rotation);
                }

                triggerData.RotationAngle = rotation.eulerAngles.y % 360;
            }

            _triggerManager.AddTrigger(triggerData);

            if (triggerData.Route != null)
            {
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);
            }

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.AddTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.updatetrigger", "awt.update")]
        private void CommandUpdateTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.UpdateTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            if (optionArgs.Length == 0)
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetTriggerOptions(player));
                return;
            }

            var newTriggerData = triggerData.Clone();
            foreach (var arg in optionArgs)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, newTriggerData, Lang.UpdateTriggerSyntax))
                    return;
            }

            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            if (triggerData.Route != null)
            {
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);
            }

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.replacetrigger", "awt.replace")]
        private void CommandReplaceTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.UpdateTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            if (optionArgs.Length == 0)
            {
                ReplyToPlayer(player, Lang.UpdateTriggerSyntax, cmd, GetTriggerOptions(player));
                return;
            }

            var newTriggerData = new TriggerData();
            foreach (var arg in optionArgs)
            {
                if (!VerifyValidArgAndModifyTrigger(player, cmd, arg, newTriggerData, Lang.UpdateTriggerSyntax))
                    return;
            }

            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            if (triggerData.Route != null)
            {
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);
            }

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.enabletrigger", "awt.enable")]
        private void CommandEnableTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            var newTriggerData = triggerData.Clone();
            newTriggerData.Enabled = true;
            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.disabletrigger", "awt.disable")]
        private void CommandDisableTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            var newTriggerData = triggerData.Clone();
            newTriggerData.Enabled = false;
            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.movetrigger", "awt.move")]
        private void CommandMoveTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            if (!VerifyAimingAtTrackPosition(player, out var trackPosition))
                return;

            if (triggerData.TriggerType == TrainTriggerType.Tunnel)
            {
                if (!VerifySupportedNearbyTrainTunnel(player, trackPosition, out var dungeonCellWrapper))
                    return;

                if (dungeonCellWrapper.TunnelType != triggerData.GetTunnelType())
                {
                    ReplyToPlayer(player, Lang.ErrorUnsupportedTunnel);
                    return;
                }

                trackPosition = dungeonCellWrapper.InverseTransformPoint(trackPosition);
            }

            _triggerManager.MoveTrigger(triggerData, trackPosition);

            var basePlayer = player.Object as BasePlayer;
            if (triggerData.Route != null)
            {
                _triggerManager.SetPlayerDisplayedRoute(basePlayer, triggerData.Route);
            }

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.MoveTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.removetrigger", "awt.remove")]
        private void CommandRemoveTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            _triggerManager.RemoveTrigger(triggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.RemoveTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.rotatetrigger", "awt.rotate")]
        private void CommandSetTriggerRotation(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            var basePlayer = player.Object as BasePlayer;
            var playerPosition = basePlayer.transform.position;

            var triggerInstance = _triggerManager.FindNearestTrigger(playerPosition, triggerData);
            var rotation = Quaternion.Euler(basePlayer.viewAngles);
            if (triggerInstance is TunnelTriggerInstance tunnelTriggerInstance)
            {
                rotation *= Quaternion.Inverse(tunnelTriggerInstance.DungeonCellWrapper.Rotation);
            }

            _triggerManager.RotateTrigger(triggerData, rotation.eulerAngles.y % 360);

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.RotateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.respawntrigger", "awt.respawn")]
        private void CommandRespawnTrigger(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.SimpleTriggerSyntax, out var triggerData, out var optionArgs))
                return;

            if (!triggerData.IsSpawner)
            {
                ReplyToPlayer(player, Lang.ErrorRequiresSpawnTrigger);
                return;
            }

            if (!triggerData.Enabled)
            {
                ReplyToPlayer(player, Lang.ErrorTriggerDisabled);
                return;
            }

            _triggerManager.RespawnTrigger(triggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("aw.addtriggercommand", "awt.addcommand", "awt.addcmd")]
        private void CommandAddCommand(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.AddCommandSyntax, out var triggerData, out var optionArgs))
                return;

            if (optionArgs.Length < 1)
            {
                ReplyToPlayer(player, Lang.AddCommandSyntax, cmd);
                return;
            }

            var quotedCommands = optionArgs.Select(command => command.Contains(" ") ? $"\"{command}\"" : command).ToArray();
            _triggerManager.AddTriggerCommand(triggerData, string.Join(" ", quotedCommands));

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.removetriggercommand", "awt.removecommand", "awt.removecmd")]
        private void CommandRemoveCommand(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.RemoveCommandSyntax, out var triggerData, out var optionArgs))
                return;

            if (optionArgs.Length < 1 || !int.TryParse(optionArgs[0], out var commandIndex))
            {
                ReplyToPlayer(player, Lang.RemoveCommandSyntax, cmd);
                return;
            }

            if (commandIndex < 1 || commandIndex > triggerData.Commands.Count)
            {
                ReplyToPlayer(player, Lang.RemoveCommandErrorIndex, commandIndex);
                return;
            }

            _triggerManager.RemoveTriggerCommand(triggerData, commandIndex - 1);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.settriggertrain", "awt.train")]
        private void CommandTriggerTrain(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyCanModifyTrigger(player, cmd, args, Lang.RemoveCommandSyntax, out var triggerData, out var optionArgs))
                return;

            var trainCarAliases = new List<string>();
            foreach (var arg in optionArgs)
            {
                var trainCarPrefab = TrainCarPrefab.FindPrefab(arg);
                if (trainCarPrefab == null)
                {
                    ReplyToPlayer(player, Lang.ErrorUnrecognizedTrainCar, arg);
                    return;
                }

                trainCarAliases.Add(trainCarPrefab.TrainCarAlias);
            }

            var newTriggerData =  triggerData.Clone();
            newTriggerData.TrainCars = trainCarAliases.ToArray();
            _triggerManager.UpdateTrigger(triggerData, newTriggerData);

            var basePlayer = player.Object as BasePlayer;
            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.UpdateTriggerSuccess, GetTriggerPrefix(player, triggerData), triggerData.Id);
        }

        [Command("aw.showtriggers", "awt.show")]
        private void CommandShowStops(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer
                || !VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return;

            var duration = 60;
            string routeName = null;

            foreach (var arg in args)
            {
                if (duration == 60)
                {
                    if (int.TryParse(arg, out var argIntValue))
                    {
                        duration = argIntValue;
                        continue;
                    }
                }

                if (routeName == null)
                {
                    var routeNameArg = GetRouteNameFromArg(arg, requirePrefix: false);
                    if (!string.IsNullOrWhiteSpace(routeNameArg))
                        routeName = routeNameArg;
                }
            }

            var basePlayer = player.Object as BasePlayer;

            _triggerManager.SetPlayerDisplayedRoute(basePlayer, routeName);
            _triggerManager.ShowAllRepeatedly(basePlayer, duration);

            if (routeName != null)
            {
                ReplyToPlayer(player, Lang.ShowTriggersWithRouteSuccess, routeName, FormatTime(duration));
            }
            else
            {
                ReplyToPlayer(player, Lang.ShowTriggersSuccess, FormatTime(duration));
            }
        }

        #endregion

        #region API

        [HookMethod(nameof(API_AutomateWorkcart))]
        public bool API_AutomateWorkcart(TrainEngine trainEngine)
        {
            return _trainManager.HasTrainController(trainEngine)
                || _trainManager.TryCreateTrainController(trainEngine);
        }

        [HookMethod(nameof(API_StopAutomatingWorkcart))]
        public void API_StopAutomatingWorkcart(TrainEngine trainEngine)
        {
            _trainManager.KillTrainController(trainEngine);
        }

        [HookMethod(nameof(API_IsWorkcartAutomated))]
        public bool API_IsWorkcartAutomated(TrainEngine trainEngine)
        {
            return _trainManager.HasTrainController(trainEngine);
        }

        [HookMethod(nameof(API_GetAutomatedWorkcarts))]
        public TrainEngine[] API_GetAutomatedWorkcarts()
        {
            return _trainManager.GetAutomatedTrainEngines();
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object OnWorkcartAutomationStart(TrainEngine trainEngine)
            {
                return Interface.CallHook("OnWorkcartAutomationStart", trainEngine);
            }

            public static void OnWorkcartAutomationStarted(TrainEngine trainEngine)
            {
                Interface.CallHook("OnWorkcartAutomationStarted", trainEngine);
            }

            public static void OnWorkcartAutomationStopped(TrainEngine trainEngine)
            {
                Interface.CallHook("OnWorkcartAutomationStopped", trainEngine);
            }
        }

        #endregion

        #region Dependencies

        private bool IsCargoTrain(TrainEngine trainEngine)
        {
            return CargoTrainEvent?.Call("IsTrainSpecial", trainEngine.net.ID) is true;
        }

        #endregion

        #region Helper Methods - Command Checks

        private bool VerifyPermission(IPlayer player, string permissionName)
        {
            if (player.HasPermission(permissionName))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoPermission);
            return false;
        }

        private bool VerifyAnyTriggers(IPlayer player)
        {
            if (_mapData.MapTriggers.Count > 0
                || _tunnelData.TunnelTriggers.Count > 0)
                return true;

            ReplyToPlayer(player, Lang.ErrorNoTriggers);
            return false;
        }

        private bool VerifyTriggerExists(IPlayer player, int triggerId, TrainTriggerType triggerType, out TriggerData triggerData)
        {
            triggerData = _triggerManager.FindTrigger(triggerId, triggerType);
            if (triggerData != null)
                return true;

            _triggerManager.ShowAllRepeatedly(player.Object as BasePlayer);
            ReplyToPlayer(player, Lang.ErrorTriggerNotFound, GetTriggerPrefix(player, triggerType), triggerId);
            return false;
        }

        private bool VerifyAimingAtTrackPosition(IPlayer player, out Vector3 trackPosition)
        {
            if (TryGetTrackPosition(player.Object as BasePlayer, out trackPosition))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoTrackFound);
            return false;
        }

        private bool IsTriggerArg(IPlayer player, string arg, out int triggerId, out TrainTriggerType triggerType)
        {
            triggerType = TrainTriggerType.Map;
            triggerId = 0;

            if (arg.StartsWith("#"))
            {
                arg = arg.Substring(1);
            }

            if (arg.Length <= 1)
                return false;

            var triggerPrefix = arg.Substring(0, 1).ToLower();
            var triggerIdString = arg.Substring(1).ToLower();

            if (!int.TryParse(triggerIdString, out triggerId))
                return false;

            if (triggerPrefix == GetTriggerPrefix(player, TrainTriggerType.Tunnel).ToLower())
            {
                triggerType = TrainTriggerType.Tunnel;
                return true;
            }

            if (triggerPrefix == GetTriggerPrefix(player, TrainTriggerType.Map).ToLower())
            {
                triggerType = TrainTriggerType.Map;
                return true;
            }

            return false;
        }

        private bool VerifyTriggerWhereAiming(IPlayer player, string cmd, string[] args, string errorMessageName, out TriggerData triggerData, out string[] optionArgs)
        {
            var basePlayer = player.Object as BasePlayer;
            optionArgs = args;
            triggerData = null;

            triggerData = _triggerManager.FindNearestTriggerWhereAiming(basePlayer);
            if (triggerData != null)
                return true;

            _triggerManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
            return false;
        }

        private bool VerifyCanModifyTrigger(IPlayer player, string cmd, string[] args, string errorMessageName, out TriggerData triggerData, out string[] optionArgs)
        {
            triggerData = null;
            optionArgs = null;

            if (!VerifyPermission(player, PermissionManageTriggers)
                || !VerifyAnyTriggers(player))
                return false;

            if (args.Length > 0 && IsTriggerArg(player, args[0], out var triggerId, out var triggerType))
            {
                optionArgs = args.Skip(1).ToArray();
                return VerifyTriggerExists(player, triggerId, triggerType, out triggerData);
            }

            if (player.IsServer)
            {
                // Server commands must specify a trigger id.
                ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
                return false;
            }

            return VerifyTriggerWhereAiming(player, cmd, args, errorMessageName, out triggerData, out optionArgs);
        }

        private bool VerifySupportedNearbyTrainTunnel(IPlayer player, Vector3 trackPosition, out DungeonCellWrapper dungeonCellWrapper)
        {
            dungeonCellWrapper = FindNearestDungeonCell(trackPosition);
            if (dungeonCellWrapper == null || dungeonCellWrapper.TunnelType == TunnelType.Unsupported)
            {
                ReplyToPlayer(player, Lang.ErrorUnsupportedTunnel);
                return false;
            }

            return true;
        }

        private bool VerifyValidArgAndModifyTrigger(IPlayer player, string cmd, string arg, TriggerData triggerData, string errorMessageName)
        {
            var argLower = arg.ToLower();
            if (argLower is "start" or "conductor")
            {
                triggerData.AddConductor = true;
                return true;
            }

            if (argLower.StartsWith("brake"))
            {
                triggerData.Brake = true;
                return true;
            }

            if (argLower.StartsWith("destroy"))
            {
                triggerData.Destroy = true;
                return true;
            }

            if (argLower.StartsWith("enable"))
            {
                triggerData.Enabled = true;
                return true;
            }

            if (argLower.StartsWith("disable"))
            {
                triggerData.Enabled = false;
                return true;
            }

            var prefab = TrainCarPrefab.FindPrefab(argLower);
            if (prefab != null)
            {
                if (triggerData.TrainCars == null)
                {
                    triggerData.TrainCars = new[] { prefab.TrainCarAlias };
                }
                else
                {
                    var length = triggerData.TrainCars.Length;
                    Array.Resize(ref triggerData.TrainCars, length + 1);
                    triggerData.TrainCars[length] = prefab.TrainCarAlias;
                }

                return true;
            }

            if (float.TryParse(arg, out var stopDuration))
            {
                triggerData.StopDuration = stopDuration;
                return true;
            }

            var routeName = GetRouteNameFromArg(arg, requirePrefix: true);
            if (!string.IsNullOrWhiteSpace(routeName))
            {
                triggerData.Route = routeName;
                return true;
            }

            if (Enum.TryParse(arg, true, out SpeedInstruction speedInstruction))
            {
                var speedString = speedInstruction.ToString();

                // If zero speed is already set, assume this is the departure speed.
                if (triggerData.Speed == SpeedInstruction.Zero.ToString())
                {
                    triggerData.DepartureSpeed = speedString;
                }
                else
                {
                    triggerData.Speed = speedString;
                }

                return true;
            }

            if (Enum.TryParse(arg, true, out DirectionInstruction directionInstruction))
            {
                triggerData.Direction = directionInstruction.ToString();
                return true;
            }

            if (Enum.TryParse(arg, true, out TrackSelectionInstruction trackSelectionInstruction))
            {
                triggerData.TrackSelection = trackSelectionInstruction.ToString();
                return true;
            }

            ReplyToPlayer(player, errorMessageName, cmd, GetTriggerOptions(player));
            return false;
        }

        #endregion

        #region Helper Methods - Coupling

        private static void UpdateAllowedCouplings(TrainCar trainCar, bool allowFront, bool allowRear)
        {
            var coupling = trainCar.coupling;
            var frontCoupling = coupling.frontCoupling;
            var rearCoupling = coupling.rearCoupling;

            if (trainCar.frontCoupling == null || trainCar.rearCoupling == null)
            {
                // Some train cars do not allow coupling, such as the classic workcart.
                return;
            }

            if (!allowFront && frontCoupling.IsCoupled)
            {
                frontCoupling.Uncouple(reflect: true);
            }

            if (!allowRear && rearCoupling.IsCoupled)
            {
                rearCoupling.Uncouple(reflect: true);
            }

            if (frontCoupling.isValid != allowFront)
            {
                TrainCouplingIsValidField.SetValue(frontCoupling, allowFront);
            }

            if (rearCoupling.isValid != allowRear)
            {
                TrainCouplingIsValidField.SetValue(rearCoupling, allowRear);
            }
        }

        private static void DisableTrainCoupling(CompleteTrain completeTrain)
        {
            var firstTrainCar = completeTrain.trainCars.FirstOrDefault();
            var lastTrainCar = completeTrain.trainCars.LastOrDefault();
            if (firstTrainCar == null || lastTrainCar == null)
                return;

            UpdateAllowedCouplings(firstTrainCar, firstTrainCar.coupling.IsFrontCoupled, firstTrainCar.coupling.IsRearCoupled);

            if (lastTrainCar != firstTrainCar)
            {
                UpdateAllowedCouplings(lastTrainCar, lastTrainCar.coupling.IsFrontCoupled, lastTrainCar.coupling.IsRearCoupled);
            }
        }

        private static void EnableTrainCoupling(CompleteTrain completeTrain)
        {
            var firstTrainCar = completeTrain.trainCars.FirstOrDefault();
            var lastTrainCar = completeTrain.trainCars.LastOrDefault();
            if (firstTrainCar == null || lastTrainCar == null)
                return;

            UpdateAllowedCouplings(firstTrainCar, allowFront: true, allowRear: true);

            if (lastTrainCar != firstTrainCar)
            {
                UpdateAllowedCouplings(lastTrainCar, allowFront: true, allowRear: true);
            }
        }

        #endregion

        #region Helper Methods

        private static int GetNextTriggerId(List<TriggerData> triggerList)
        {
            var highestTriggerId = 0;

            foreach (var triggerData in triggerList)
            {
                highestTriggerId = Math.Max(highestTriggerId, triggerData.Id);
            }

            return highestTriggerId + 1;
        }

        private static string GetRouteNameFromArg(string routeName, bool requirePrefix = true)
        {
            if (routeName.StartsWith("@"))
                return routeName[1..];

            return requirePrefix ? null : routeName;
        }

        private IEnumerator DoStartupRoutine()
        {
            yield return _triggerManager.CreateAll();
            TrackStart();

            var foundTrainEngineIds = new HashSet<ulong>();
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var trainEngine = entity as TrainEngine;
                if (trainEngine == null)
                    continue;

                var trainEngineData = _data.GetTrainEngineData(trainEngine.net.ID.Value);
                if (trainEngineData != null)
                {
                    foundTrainEngineIds.Add(trainEngine.net.ID.Value);

                    var trainEngine2 = trainEngine;
                    var trainEngineData2 = trainEngineData;
                    timer.Once(UnityEngine.Random.Range(0, 1f), () =>
                    {
                        if (trainEngine2 != null
                            && !IsTrainOwned(trainEngine2)
                            && _trainManager.CanHaveMoreConductors()
                            && !_trainManager.HasTrainController(trainEngine2))
                        {
                            _trainManager.TryCreateTrainController(trainEngine2, trainEngineData: trainEngineData2);
                        }
                    });
                }
            }

            _data.TrimToTrainEngineIds(foundTrainEngineIds);
            TrackEnd();
        }

        private bool AutomationWasBlocked(TrainEngine trainEngine)
        {
            if (ExposedHooks.OnWorkcartAutomationStart(trainEngine) is false)
                return true;

            if (IsCargoTrain(trainEngine))
                return true;

            return false;
        }

        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Automated Workcarts] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Automated Workcarts] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Automated Workcarts] {message}");

        private static float GetThrottleFraction(EngineSpeeds throttle)
        {
            switch (throttle)
            {
                case EngineSpeeds.Rev_Hi: return -1;
                case EngineSpeeds.Rev_Med: return -0.5f;
                case EngineSpeeds.Rev_Lo: return -0.2f;
                case EngineSpeeds.Fwd_Lo: return 0.2f;
                case EngineSpeeds.Fwd_Med: return 0.5f;
                case EngineSpeeds.Fwd_Hi: return 1;
                default: return 0;
            }
        }

        private static TrainEngine GetLeadTrainEngine(CompleteTrain completeTrain)
        {
            if (completeTrain.PrimaryTrainCar == completeTrain.trainCars[0])
            {
                for (var i = 0; i < completeTrain.trainCars.Count; i++)
                {
                    var trainEngine = completeTrain.trainCars[i] as TrainEngine;
                    if ((object)trainEngine != null)
                        return trainEngine;
                }
            }
            else
            {
                for (var i = completeTrain.trainCars.Count - 1; i >= 0; i--)
                {
                    var trainEngine = completeTrain.trainCars[i] as TrainEngine;
                    if ((object)trainEngine != null)
                        return trainEngine;
                }
            }

            return null;
        }

        private static TrainEngine GetLeadTrainEngine(TrainCar trainCar)
        {
            var trainEngine = trainCar as TrainEngine;
            if (trainEngine != null)
                return trainEngine;

            return GetLeadTrainEngine(trainCar.completeTrain);
        }

        private static void DetermineTrainCarOrientations(TrainCar trainCar, Vector3 forward, TrainCar otherTrainCar, out TrainCar forwardTrainCar)
        {
            var position = trainCar.transform.position;
            var otherPosition = otherTrainCar.transform.position;
            var forwardPosition = position + forward * 100f;

            forwardTrainCar = trainCar;

            if ((forwardPosition - position).sqrMagnitude > (forwardPosition - otherPosition).sqrMagnitude)
            {
                forwardTrainCar = otherTrainCar;
            }
        }

        private static Vector3 GetTrainCarForward(TrainCar trainCar)
        {
            return trainCar.GetTrackSpeed() >= 0
                ? trainCar.transform.forward
                : -trainCar.transform.forward;
        }

        private static void EnableInvincibility(TrainCar trainCar)
        {
            trainCar.initialSpawnTime = float.MaxValue;
        }

        private static void DisableInvincibility(TrainCar trainCar)
        {
            trainCar.initialSpawnTime = Time.time;
        }

        private static void EnableSavingRecursive(BaseEntity entity, bool enableSaving)
        {
            entity.EnableSaving(enableSaving);

            foreach (var child in entity.children)
            {
                if (child is BasePlayer)
                    continue;

                EnableSavingRecursive(child, enableSaving);
            }
        }

        private static TrainCar SpawnTrainCar(string prefabName, Vector3 position, Quaternion rotation)
        {
            var trainCar = GameManager.server.CreateEntity(prefabName, position, rotation) as TrainCar;
            if (trainCar == null)
                return null;

            // Ensure the train car does not decay for some time.
            trainCar.lastDecayTick = Time.realtimeSinceStartup;

            trainCar.limitNetworking = true;
            trainCar.EnableSaving(false);
            trainCar.Spawn();

            if (trainCar.IsDestroyed)
                return null;

            // Don't let the train kill itself if there isn't quite enough space.
            trainCar.CancelInvoke(trainCar.KillMessage);

            trainCar.Invoke(() => EnableSavingRecursive(trainCar, false), 0);

            return trainCar;
        }

        private static float GetSplineDistance(TrainTrackSpline spline, Vector3 position)
        {
            spline.GetDistance(position, 1, out var distanceOnSpline);
            return distanceOnSpline;
        }

        private static TrainCar AddTrainCar(TrainCar frontTrainCar, TrainCarPrefab frontTrainCarPrefab, TrainCarPrefab trainCarPrefab, TrackSelection trackSelection)
        {
            var rearCouplingTransform = frontTrainCarPrefab.Reverse
                ? frontTrainCar.frontCoupling
                : frontTrainCar.rearCoupling;

            if (rearCouplingTransform == null)
                return null;

            var rearWheelPos = frontTrainCarPrefab.Reverse
                ? frontTrainCar.GetFrontWheelPos()
                : frontTrainCar.GetRearWheelPos();

            var wheelToRearCouplingDistance = Math.Abs(rearWheelPos.z - rearCouplingTransform.position.z);

            var rearSpline = frontTrainCarPrefab.Reverse
                ? frontTrainCar.FrontTrackSection
                : frontTrainCar.RearTrackSection;

            var rearWheelDistanceOnSpline = GetSplineDistance(rearSpline, rearWheelPos);

            var frontTrainCarForward = frontTrainCarPrefab.Reverse
                ? -frontTrainCar.transform.forward
                : frontTrainCar.transform.forward;

            var askerIsForward = rearSpline.IsForward(frontTrainCarForward, rearWheelDistanceOnSpline);
            var splineInfo = new SplineInfo
            {
                Spline = rearSpline,
                Distance = rearWheelDistanceOnSpline,
                Ascending = !askerIsForward,
                IsForward = askerIsForward,
            };

            // Spawn the train slightly farther away so it has space. It will be moved forward after spawn.
            var spawnDistanceOffset = 2;

            var finalDistance = wheelToRearCouplingDistance + GetTrainCarFrontCouplingOffsetZ(trainCarPrefab);

            var finalPosition = GetPositionAlongTrack(splineInfo, finalDistance, trackSelection, out var finalSplineInfo);

            var resultPosition = GetPositionAlongTrack(finalSplineInfo, spawnDistanceOffset, trackSelection, out var spawnSplineInfo);
            var resultRotation = GetSplineTangentRotation(spawnSplineInfo.Spline, spawnSplineInfo.Distance, frontTrainCar.transform.rotation);

            if (trainCarPrefab.Reverse != frontTrainCarPrefab.Reverse)
            {
                resultRotation = Quaternion.LookRotation(resultRotation * -Vector3.forward);
            }

            // TODO: Fix issue where workcarts jump on start, when first two are both reverse

            var rearTrainCar = SpawnTrainCar(trainCarPrefab.PrefabPath, resultPosition, resultRotation);
            if (rearTrainCar != null)
            {
                if (rearTrainCar.FrontTrackSection == null)
                {
                    rearTrainCar.Kill();
                    return null;
                }

                rearTrainCar.MoveFrontWheelsAlongTrackSpline(
                    rearTrainCar.FrontTrackSection,
                    rearTrainCar.FrontWheelSplineDist,
                    spawnDistanceOffset,
                    rearTrainCar.RearTrackSection != rearTrainCar.FrontTrackSection ? rearTrainCar.RearTrackSection : null,
                    trackSelection
                );

                rearTrainCar.transform.position = finalPosition;

                var frontCoupling = frontTrainCarPrefab.Reverse
                    ? frontTrainCar.coupling.frontCoupling
                    : frontTrainCar.coupling.rearCoupling;

                var rearCoupling = trainCarPrefab.Reverse
                    ? rearTrainCar.coupling.rearCoupling
                    : rearTrainCar.coupling.frontCoupling;

                frontCoupling.TryCouple(rearCoupling, reflect: true);
            }

            return rearTrainCar;
        }

        private static float GetTrainCarFrontCouplingOffsetZ(TrainCarPrefab trainCarPrefab)
        {
            var prefab = GameManager.server.FindPrefab(trainCarPrefab.PrefabPath)?.GetComponent<TrainCar>();
            if (prefab == null)
                return 0;

            return trainCarPrefab.Reverse
                ? prefab.rearCoupling.localPosition.z
                : prefab.frontCoupling.localPosition.z;
        }

        private static ConnectedTrackInfo GetAdjacentTrackInfo(TrainTrackSpline spline, TrackSelection selection, bool isAscending = true, bool askerIsForward = true)
        {
            var trackOptions = isAscending
                ? spline.nextTracks
                : spline.prevTracks;

            if (trackOptions.Count == 0)
                return null;

            if (trackOptions.Count == 1)
                return trackOptions[0];

            switch (selection)
            {
                case TrackSelection.Left:
                    return isAscending == askerIsForward
                        ? trackOptions.FirstOrDefault()
                        : trackOptions.LastOrDefault();

                case TrackSelection.Right:
                    return isAscending == askerIsForward
                        ? trackOptions.LastOrDefault()
                        : trackOptions.FirstOrDefault();

                default:
                    return trackOptions[isAscending ? spline.straightestNextIndex : spline.straightestPrevIndex];
            }
        }

        private static Quaternion GetSplineTangentRotation(TrainTrackSpline spline, float distanceOnSpline, Quaternion approximateRotation)
        {
            spline.GetPositionAndTangent(distanceOnSpline, approximateRotation * Vector3.forward, out var tangentDirection);
            return Quaternion.LookRotation(tangentDirection);
        }

        private struct SplineInfo
        {
            public TrainTrackSpline Spline;
            public float Distance;
            public bool Ascending;
            public bool IsForward;
        }

        private static Vector3 GetPositionAlongTrack(SplineInfo splineInfo, float desiredDistance, TrackSelection trackSelection, out SplineInfo resultSplineInfo, out float remainingDistance)
        {
            resultSplineInfo = splineInfo;
            remainingDistance = desiredDistance;

            var i = 0;

            while (remainingDistance > 0)
            {
                if (i++ > 1000)
                {
                    LogError("Something is wrong. Please contact the plugin developer.");
                    return Vector3.zero;
                }

                var splineLength = resultSplineInfo.Spline.GetLength();
                var newDistanceOnSpline = resultSplineInfo.Ascending
                    ? resultSplineInfo.Distance + remainingDistance
                    : resultSplineInfo.Distance - remainingDistance;

                remainingDistance -= resultSplineInfo.Ascending
                    ? splineLength - resultSplineInfo.Distance
                    : resultSplineInfo.Distance;

                if (newDistanceOnSpline >= 0 && newDistanceOnSpline <= splineLength)
                {
                    // Reached desired distance.
                    resultSplineInfo.Distance = newDistanceOnSpline;
                    return resultSplineInfo.Spline.GetPosition(resultSplineInfo.Distance);
                }

                var adjacentTrackInfo = GetAdjacentTrackInfo(resultSplineInfo.Spline, trackSelection, resultSplineInfo.Ascending, resultSplineInfo.IsForward);
                if (adjacentTrackInfo == null)
                {
                    // Track is a dead end.
                    resultSplineInfo.Distance = resultSplineInfo.Ascending ? splineLength : 0;
                    return resultSplineInfo.Spline.GetPosition(resultSplineInfo.Distance);
                }

                if (adjacentTrackInfo.orientation == TrackOrientation.Reverse)
                {
                    resultSplineInfo.Ascending = !resultSplineInfo.Ascending;
                    resultSplineInfo.IsForward = !resultSplineInfo.IsForward;
                }

                resultSplineInfo.Spline = adjacentTrackInfo.track;
                resultSplineInfo.Distance = resultSplineInfo.Ascending ? 0 : resultSplineInfo.Spline.GetLength();
            }

            return Vector3.zero;
        }

        private static Vector3 GetPositionAlongTrack(SplineInfo splineInfo, float desiredDistance, TrackSelection trackSelection, out SplineInfo resultSplineInfo)
        {
            return GetPositionAlongTrack(splineInfo, desiredDistance, trackSelection, out resultSplineInfo, out var remainingDistance);
        }

        private static bool IsTrainOwned(TrainCar trainCar)
        {
            foreach (var car in trainCar.completeTrain.trainCars)
            {
                if (car.OwnerID != 0)
                    return true;
            }

            return false;
        }

        private static string GetShortName(string prefabName)
        {
            var slashIndex = prefabName.LastIndexOf("/");
            var baseName = (slashIndex == -1) ? prefabName : prefabName.Substring(slashIndex + 1);
            return baseName.Replace(".prefab", "");
        }

        private static bool TryParseEngineSpeed(string speedName, out EngineSpeeds engineSpeed)
        {
            if (Enum.TryParse(speedName, true, out engineSpeed))
                return true;

            engineSpeed = EngineSpeeds.Zero;
            LogError($"Unrecognized engine speed: {speedName}");
            return false;
        }

        private static bool TryParseTrackSelection(string selectionName, out TrackSelection trackSelection)
        {
            if (Enum.TryParse(selectionName, true, out trackSelection))
                return true;

            LogError($"Unrecognized track selection: {selectionName}");
            trackSelection = TrackSelection.Default;
            return false;
        }

        private static string FormatOptions(ICollection<string> optionNames, string delimiter = " | ")
        {
            var formattedOptionNames = new string[optionNames.Count];

            var i = 0;
            foreach (var optionName in optionNames)
            {
                formattedOptionNames[i] = $"<color=#fd4>{optionName}</color>";
                i++;
            }

            return string.Join(delimiter, formattedOptionNames);
        }

        private static string GetEnumOptions<T>()
        {
            return FormatOptions(Enum.GetNames(typeof(T)));
        }

        private static bool TryGetHitPosition(BasePlayer player, out Vector3 position, float maxDistance)
        {
            if (Physics.Raycast(player.eyes.HeadRay(), out var hit, maxDistance, Layers.Solid, QueryTriggerInteraction.Ignore))
            {
                position = hit.point;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private static bool TryGetTrackPosition(BasePlayer player, out Vector3 trackPosition, float maxDistance = 30)
        {
            if (!TryGetHitPosition(player, out var hitPosition, maxDistance))
            {
                trackPosition = Vector3.zero;
                return false;
            }

            if (!TryFindTrackNear(hitPosition, 5, out var spline, out var distanceResult))
            {
                trackPosition = Vector3.zero;
                return false;
            }

            trackPosition = spline.GetPosition(distanceResult);
            return true;
        }

        private static TrainTrigger GetHitTrigger(BasePlayer player, float maxDistance = 30)
        {
            return Physics.Raycast(player.eyes.HeadRay(), out var hit, maxDistance, 1 << TrainTrigger.TriggerLayer, QueryTriggerInteraction.Collide)
                ? hit.collider.GetComponent<TrainTrigger>()
                : null;
        }

        private static DungeonCellWrapper FindNearestDungeonCell(Vector3 position)
        {
            DungeonGridCell closestDungeon = null;
            var shortestSqrDistance = float.MaxValue;

            foreach (var dungeon in TerrainMeta.Path.DungeonGridCells)
            {
                var dungeonCellWrapper = new DungeonCellWrapper(dungeon);
                if (dungeonCellWrapper.TunnelType == TunnelType.Unsupported)
                    continue;

                if (!dungeonCellWrapper.IsInBounds(position))
                    continue;

                var sqrDistance = (dungeon.transform.position - position).sqrMagnitude;
                if (sqrDistance < shortestSqrDistance)
                {
                    shortestSqrDistance = sqrDistance;
                    closestDungeon = dungeon;
                }
            }

            return closestDungeon == null ? null : new DungeonCellWrapper(closestDungeon);
        }

        private static List<DungeonCellWrapper> FindAllTunnelsOfType(TunnelType tunnelType)
        {
            var dungeonCellList = new List<DungeonCellWrapper>();

            foreach (var dungeonCell in TerrainMeta.Path.DungeonGridCells)
            {
                if (DungeonCellWrapper.GetTunnelType(dungeonCell) == tunnelType)
                {
                    dungeonCellList.Add(new DungeonCellWrapper(dungeonCell));
                }
            }

            return dungeonCellList;
        }

        private static BaseEntity GetLookEntity(BasePlayer player, int layerMask = Physics.DefaultRaycastLayers, float maxDistance = 20)
        {
            return Physics.Raycast(player.eyes.HeadRay(), out var hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static TrainCar GetTrainCarWhereAiming(BasePlayer player)
        {
            return GetLookEntity(player, Layers.Mask.Vehicle_Detailed) as TrainCar;
        }

        private static void DestroyTrainCarCinematically(TrainCar trainCar)
        {
            if (trainCar.IsDestroyed)
                return;

            if (trainCar.CarType != TrainCarType.Engine)
            {
                Effect.server.Run(BradleyExplosionEffectPrefab, trainCar.GetExplosionPos(), Vector3.up, sourceConnection: null, broadcast: true);
            }

            var hitInfo = new HitInfo(null, trainCar, DamageType.Explosion, float.MaxValue, trainCar.transform.position)
            {
                UseProtection = false,
            };
            trainCar.Die(hitInfo);
        }

        private static void ScheduleDestroyTrainCarCinematically(TrainCar trainCar)
        {
            trainCar.Invoke(() => DestroyTrainCarCinematically(trainCar), 0);
        }

        private static bool CollectionsEqual<T>(ICollection<T> collectionA, ICollection<T> collectionB)
        {
            var countA = collectionA?.Count ?? 0;
            var countB = collectionB?.Count ?? 0;

            if (countA != countB)
                return false;

            if (countA == 0 && countB == 0)
                return true;

            return collectionA?.SequenceEqual(collectionB) ?? false;
        }

        private static string FormatTime(double seconds)
        {
            return TimeSpan.FromSeconds(seconds).ToString("g");
        }

        #endregion

        #region Train Car Prefabs

        private class TrainCarPrefab
        {
            public const string WorkcartAlias = "Workcart";

            public const string ClassicWorkcartPrefab = "assets/content/vehicles/trains/workcart/workcart.entity.prefab";
            private const string LocomotivePrefab = "assets/content/vehicles/trains/locomotive/locomotive.entity.prefab";
            private const string SedanPrefab = "assets/content/vehicles/sedan_a/sedanrail.entity.prefab";
            private const string WorkcartPrefab = "assets/content/vehicles/trains/workcart/workcart_aboveground.entity.prefab";
            private const string WorkcartCoveredPrefab = "assets/content/vehicles/trains/workcart/workcart_aboveground2.entity.prefab";
            private const string WagonAPrefab = "assets/content/vehicles/trains/wagons/trainwagona.entity.prefab";
            private const string WagonBPrefab = "assets/content/vehicles/trains/wagons/trainwagonb.entity.prefab";
            private const string WagonCPrefab = "assets/content/vehicles/trains/wagons/trainwagonc.entity.prefab";
            private const string WagonFuelPrefab = "assets/content/vehicles/trains/wagons/trainwagonunloadablefuel.entity.prefab";
            private const string WagonLootPrefab = "assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab";
            private const string WagonResourcePrefab = "assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab";
            private const string CaboosePrefab = "assets/content/vehicles/trains/caboose/traincaboose.entity.prefab";

            private static readonly Dictionary<string, TrainCarPrefab> AllowedPrefabs = new(StringComparer.InvariantCultureIgnoreCase)
            {
                ["Locomotive"] = new TrainCarPrefab("Locomotive", LocomotivePrefab),
                ["Sedan"] = new TrainCarPrefab("Sedan", SedanPrefab),
                [WorkcartAlias] = new TrainCarPrefab(WorkcartAlias, WorkcartPrefab),
                ["WorkcartCovered"] = new TrainCarPrefab("WorkcartCovered", WorkcartCoveredPrefab),
                ["WagonA"] = new TrainCarPrefab("WagonA", WagonAPrefab),
                ["WagonB"] = new TrainCarPrefab("WagonB", WagonBPrefab),
                ["WagonC"] = new TrainCarPrefab("WagonC", WagonCPrefab),
                ["WagonFuel"] = new TrainCarPrefab("WagonFuel", WagonFuelPrefab),
                ["WagonLoot"] = new TrainCarPrefab("WagonLoot", WagonLootPrefab),
                ["WagonResource"] = new TrainCarPrefab("WagonResource", WagonResourcePrefab),
                ["Caboose"] = new TrainCarPrefab("Caboose", CaboosePrefab),

                ["Locomotive_R"] = new TrainCarPrefab("Locomotive_R", LocomotivePrefab, reverse: true),
                ["Sedan_R"] = new TrainCarPrefab("Sedan", SedanPrefab),
                [$"{WorkcartAlias}_R"] = new TrainCarPrefab($"{WorkcartAlias}_R", WorkcartPrefab, reverse: true),
                ["WorkcartCovered_R"] = new TrainCarPrefab("WorkcartCovered_R", WorkcartCoveredPrefab, reverse: true),
                ["WagonA_R"] = new TrainCarPrefab("WagonA_R", WagonAPrefab, reverse: true),
                ["WagonB_R"] = new TrainCarPrefab("WagonB_R", WagonBPrefab, reverse: true),
                ["WagonC_R"] = new TrainCarPrefab("WagonC_R", WagonCPrefab, reverse: true),
                ["WagonFuel_R"] = new TrainCarPrefab("WagonFuel_R", WagonFuelPrefab, reverse: true),
                ["WagonLoot_R"] = new TrainCarPrefab("WagonLoot_R", WagonLootPrefab, reverse: true),
                ["WagonResource_R"] = new TrainCarPrefab("WagonResource_R", WagonResourcePrefab, reverse: true),
                ["Caboose_R"] = new TrainCarPrefab("Caboose_R", CaboosePrefab, reverse: true),
            };

            public static TrainCarPrefab FindPrefab(string trainCarAlias)
            {
                return AllowedPrefabs.TryGetValue(trainCarAlias, out var trainCarPrefab)
                    ? trainCarPrefab
                    : null;
            }

            public static ICollection<string> GetAliases()
            {
                return AllowedPrefabs.Keys;
            }

            public string TrainCarAlias;
            public string PrefabPath;
            public bool Reverse;

            public TrainCarPrefab(string trainCarAlias, string prefabPath, bool reverse = false)
            {
                TrainCarAlias = trainCarAlias;
                PrefabPath = prefabPath;
                Reverse = reverse;
            }
        }

        #endregion

        #region Dungeon Cells

        private enum TunnelType
        {
            // Don't rename these since the names are persisted in data files.
            TrainStation,
            BarricadeTunnel,
            LootTunnel,
            Intersection,
            VerticalIntersection,
            LargeIntersection,
            Unsupported
        }

        private static readonly Dictionary<string, Quaternion> DungeonRotations = new()
        {
            ["station-sn-0"] = Quaternion.Euler(0, 180, 0),
            ["station-sn-1"] = Quaternion.identity,
            ["station-sn-2"] = Quaternion.Euler(0, 180, 0),
            ["station-sn-3"] = Quaternion.identity,
            ["station-we-0"] = Quaternion.Euler(0, 90, 0),
            ["station-we-1"] = Quaternion.Euler(0, -90, 0),
            ["station-we-2"] = Quaternion.Euler(0, 90, 0),
            ["station-we-3"] = Quaternion.Euler(0, -90, 0),

            ["straight-sn-0"] = Quaternion.identity,
            ["straight-sn-1"] = Quaternion.Euler(0, 180, 0),
            ["straight-we-0"] = Quaternion.Euler(0, -90, 0),
            ["straight-we-1"] = Quaternion.Euler(0, 90, 0),

            ["straight-sn-4"] = Quaternion.identity,
            ["straight-sn-5"] = Quaternion.Euler(0, 180, 0),
            ["straight-we-4"] = Quaternion.Euler(0, -90, 0),
            ["straight-we-5"] = Quaternion.Euler(0, 90, 0),

            ["intersection-n"] = Quaternion.identity,
            ["intersection-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-w"] = Quaternion.Euler(0, -90, 0),

            ["intersection"] = Quaternion.identity,

            ["intersection-b1-n"] = Quaternion.identity,
            ["intersection-b1-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-b1-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-b1-w"] = Quaternion.Euler(0, 270, 0),

            ["intersection-b2-n"] = Quaternion.identity,
            ["intersection-b2-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-b2-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-b2-w"] = Quaternion.Euler(0, 270, 0),

            ["intersection-b3-n"] = Quaternion.identity,
            ["intersection-b3-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-b3-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-b3-w"] = Quaternion.Euler(0, 270, 0),

            ["intersection-b4-n"] = Quaternion.identity,
            ["intersection-b4-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-b4-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-b4-w"] = Quaternion.Euler(0, 270, 0),

            ["intersection-b5-n"] = Quaternion.identity,
            ["intersection-b5-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-b5-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-b5-w"] = Quaternion.Euler(0, 270, 0),

            ["intersection-b6-n"] = Quaternion.identity,
            ["intersection-b6-e"] = Quaternion.Euler(0, 90, 0),
            ["intersection-b6-s"] = Quaternion.Euler(0, 180, 0),
            ["intersection-b6-w"] = Quaternion.Euler(0, 270, 0),
        };

        private static readonly Dictionary<string, TunnelType> DungeonCellTypes = new()
        {
            ["station-sn-0"] = TunnelType.TrainStation,
            ["station-sn-1"] = TunnelType.TrainStation,
            ["station-sn-2"] = TunnelType.TrainStation,
            ["station-sn-3"] = TunnelType.TrainStation,
            ["station-we-0"] = TunnelType.TrainStation,
            ["station-we-1"] = TunnelType.TrainStation,
            ["station-we-2"] = TunnelType.TrainStation,
            ["station-we-3"] = TunnelType.TrainStation,

            ["straight-sn-0"] = TunnelType.LootTunnel,
            ["straight-sn-1"] = TunnelType.LootTunnel,
            ["straight-we-0"] = TunnelType.LootTunnel,
            ["straight-we-1"] = TunnelType.LootTunnel,

            ["straight-sn-4"] = TunnelType.BarricadeTunnel,
            ["straight-sn-5"] = TunnelType.BarricadeTunnel,
            ["straight-we-4"] = TunnelType.BarricadeTunnel,
            ["straight-we-5"] = TunnelType.BarricadeTunnel,

            ["intersection-n"] = TunnelType.Intersection,
            ["intersection-e"] = TunnelType.Intersection,
            ["intersection-s"] = TunnelType.Intersection,
            ["intersection-w"] = TunnelType.Intersection,

            ["intersection"] = TunnelType.LargeIntersection,

            ["intersection-b1-n"] = TunnelType.VerticalIntersection,
            ["intersection-b1-e"] = TunnelType.VerticalIntersection,
            ["intersection-b1-s"] = TunnelType.VerticalIntersection,
            ["intersection-b1-w"] = TunnelType.VerticalIntersection,

            ["intersection-b2-n"] = TunnelType.VerticalIntersection,
            ["intersection-b2-e"] = TunnelType.VerticalIntersection,
            ["intersection-b2-s"] = TunnelType.VerticalIntersection,
            ["intersection-b2-w"] = TunnelType.VerticalIntersection,

            ["intersection-b3-n"] = TunnelType.VerticalIntersection,
            ["intersection-b3-e"] = TunnelType.VerticalIntersection,
            ["intersection-b3-s"] = TunnelType.VerticalIntersection,
            ["intersection-b3-w"] = TunnelType.VerticalIntersection,

            ["intersection-b4-n"] = TunnelType.VerticalIntersection,
            ["intersection-b4-e"] = TunnelType.VerticalIntersection,
            ["intersection-b4-s"] = TunnelType.VerticalIntersection,
            ["intersection-b4-w"] = TunnelType.VerticalIntersection,

            ["intersection-b5-n"] = TunnelType.VerticalIntersection,
            ["intersection-b5-e"] = TunnelType.VerticalIntersection,
            ["intersection-b5-s"] = TunnelType.VerticalIntersection,
            ["intersection-b5-w"] = TunnelType.VerticalIntersection,

            ["intersection-b6-n"] = TunnelType.VerticalIntersection,
            ["intersection-b6-e"] = TunnelType.VerticalIntersection,
            ["intersection-b6-s"] = TunnelType.VerticalIntersection,
            ["intersection-b6-w"] = TunnelType.VerticalIntersection,
        };

        private static readonly Dictionary<TunnelType, Vector3> DungeonCellDimensions = new()
        {
            [TunnelType.TrainStation] = new Vector3(108, 8.5f, 216),
            [TunnelType.BarricadeTunnel] = new Vector3(16.5f, 8.5f, 216),
            [TunnelType.LootTunnel] = new Vector3(16.5f, 8.5f, 216),
            [TunnelType.Intersection] = new Vector3(216, 8.5f, 216),
            [TunnelType.LargeIntersection] = new Vector3(216, 8.5f, 216),
            [TunnelType.VerticalIntersection] = new Vector3(216, 8.5f, 216),
        };

        private class DungeonCellWrapper
        {
            public static TunnelType GetTunnelType(DungeonGridCell dungeonCell)
            {
                return GetTunnelType(GetShortName(dungeonCell.name));
            }

            private static TunnelType GetTunnelType(string shortName)
            {
                return DungeonCellTypes.TryGetValue(shortName, out var tunnelType)
                    ? tunnelType
                    : TunnelType.Unsupported;
            }

            public static Quaternion GetRotation(string shortName)
            {
                return DungeonRotations.TryGetValue(shortName, out var rotation)
                    ? rotation
                    : Quaternion.identity;
            }

            public string ShortName { get; }
            public TunnelType TunnelType { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }

            private OBB _boundingBox;

            public DungeonCellWrapper(DungeonGridCell dungeonCell)
            {
                ShortName = GetShortName(dungeonCell.name);
                TunnelType = GetTunnelType(ShortName);
                Position = dungeonCell.transform.position;
                Rotation = GetRotation(ShortName);

                if (DungeonCellDimensions.TryGetValue(TunnelType, out var dimensions))
                {
                    _boundingBox = new OBB(Position + new Vector3(0, dimensions.y / 2, 0), dimensions, Rotation);
                }
            }

            public Vector3 InverseTransformPoint(Vector3 worldPosition)
            {
                return Quaternion.Inverse(Rotation) * (worldPosition - Position);
            }

            public Vector3 TransformPoint(Vector3 localPosition)
            {
                return Position + Rotation * localPosition;
            }

            public bool IsInBounds(Vector3 position) => _boundingBox.Contains(position);
        }

        #endregion

        #region Train Triggers

        private enum SpeedInstruction
        {
            // Don't rename these since the names are persisted in data files.
            Zero = 0,
            Lo = 1,
            Med = 2,
            Hi = 3,
        }

        private enum DirectionInstruction
        {
            // Don't rename these since the names are persisted in data files.
            Fwd,
            Rev,
            Invert,
        }

        private enum TrackSelectionInstruction
        {
            // Don't rename these since the names are persisted in data files.
            Default,
            Left,
            Right,
            Swap,
        }

        private static int EngineThrottleToNumber(EngineSpeeds throttle)
        {
            switch (throttle)
            {
                case EngineSpeeds.Fwd_Hi: return 3;
                case EngineSpeeds.Fwd_Med: return 2;
                case EngineSpeeds.Fwd_Lo: return 1;
                case EngineSpeeds.Rev_Lo: return -1;
                case EngineSpeeds.Rev_Med: return -2;
                case EngineSpeeds.Rev_Hi: return -3;
                default: return 0;
            }
        }

        private static EngineSpeeds EngineThrottleFromNumber(int speedNumber)
        {
            switch (speedNumber)
            {
                case 3: return EngineSpeeds.Fwd_Hi;
                case 2: return EngineSpeeds.Fwd_Med;
                case 1: return EngineSpeeds.Fwd_Lo;
                case -1: return EngineSpeeds.Rev_Lo;
                case -2: return EngineSpeeds.Rev_Med;
                case -3: return EngineSpeeds.Rev_Hi;
                default: return EngineSpeeds.Zero;
            }
        }

        private static int ApplySpeed(int throttle, SpeedInstruction? speedInstruction)
        {
            if (speedInstruction == null)
                return throttle;

            var sign = throttle >= 0 ? 1 : -1;
            return sign * (int)speedInstruction.Value;
        }

        private static int ApplyDirection(int throttle, DirectionInstruction? directionInstruction)
        {
            switch (directionInstruction)
            {
                case DirectionInstruction.Fwd:
                    return Math.Abs(throttle);
                case DirectionInstruction.Rev:
                    return -Math.Abs(throttle);
                case DirectionInstruction.Invert:
                    return -throttle;
                default:
                    return throttle;
            }
        }

        private static EngineSpeeds ApplyDirection(EngineSpeeds throttle, DirectionInstruction? directionInstruction)
        {
            return EngineThrottleFromNumber(ApplyDirection(EngineThrottleToNumber(throttle), directionInstruction));
        }

        private static EngineSpeeds ApplySpeedAndDirection(EngineSpeeds currentThrottle, SpeedInstruction? speedInstruction, DirectionInstruction? directionInstruction)
        {
            var throttleNumber = EngineThrottleToNumber(currentThrottle);
            throttleNumber = ApplySpeed(throttleNumber, speedInstruction);
            throttleNumber = ApplyDirection(throttleNumber, directionInstruction);
            return EngineThrottleFromNumber(throttleNumber);
        }

        private static TrackSelection ApplyTrackSelection(TrackSelection trackSelection, TrackSelectionInstruction? trackSelectionInstruction)
        {
            switch (trackSelectionInstruction)
            {
                case TrackSelectionInstruction.Default:
                    return TrackSelection.Default;

                case TrackSelectionInstruction.Left:
                    return TrackSelection.Left;

                case TrackSelectionInstruction.Right:
                    return TrackSelection.Right;

                case TrackSelectionInstruction.Swap:
                    return trackSelection == TrackSelection.Left
                        ? TrackSelection.Right
                        : trackSelection == TrackSelection.Right
                        ? TrackSelection.Left
                        : trackSelection;

                default:
                    return trackSelection;
            }
        }

        private enum TrainTriggerType { Map, Tunnel }

        private class TrainTrigger : TriggerBase
        {
            public static TrainTrigger AddToGameObject(AutomatedWorkcarts plugin, GameObject gameObject, TrainManager trainManager, TriggerData triggerData, BaseTriggerInstance triggerInstance)
            {
                var trainTrigger = gameObject.AddComponent<TrainTrigger>();
                trainTrigger._plugin = plugin;
                trainTrigger._trainManager = trainManager;
                trainTrigger._triggerInstance = triggerInstance;
                trainTrigger.TriggerData = triggerData;
                trainTrigger.interestLayers = Layers.Mask.Vehicle_World;
                return trainTrigger;
            }

            public const int TriggerLayer = 6;
            public const float TriggerRadius = 1f;

            public TriggerData TriggerData { get; private set; }
            private AutomatedWorkcarts _plugin;
            private BaseTriggerInstance _triggerInstance;
            private TrainManager _trainManager;

            public override void OnEntityEnter(BaseEntity entity)
            {
                _plugin.TrackStart();
                var trainCar = entity as TrainCar;
                if (trainCar != null)
                {
                    HandleTrainCar(trainCar);
                }

                _plugin.TrackEnd();
            }

            private bool ShouldAutomateTrain(TrainCar trainCar, out bool shouldCount)
            {
                shouldCount = true;

                if (TriggerData.IsSpawner)
                {
                    // Hybrid Spawner/Conductor triggers should only automate trains spawned by the same trigger.
                    if (!_triggerInstance.DidSpawnTrain(trainCar))
                        return false;

                    // Trains spawned by hybrid Spawner/Conductor triggers may be exempt from conductor limits.
                    shouldCount = _plugin._config.SpawnTriggersRespectConductorLimit;
                    if (!shouldCount)
                        return true;
                }

                // Don't add a conductor if the limit is reached, unless the train was spawned by this trigger.
                return _trainManager.CanHaveMoreConductors();
            }

            private void HandleTrainCar(TrainCar trainCar)
            {
                entityContents ??= new HashSet<BaseEntity>();

                // Ignore the trigger if the train car is already colliding with it.
                if (!entityContents.Add(trainCar))
                    return;

                var trainController = _trainManager.GetTrainController(trainCar);
                if (trainController == null)
                {
                    // If there is no train controller, we only care about conductor triggers.
                    if (!TriggerData.AddConductor)
                        return;

                    // Don't handle conductor triggers that are also destroy triggers since that indicates an incorrect setup.
                    if (TriggerData.Destroy)
                        return;

                    // Make sure the train has at least one train engine.
                    var leadTrainEngine = GetLeadTrainEngine(trainCar);
                    if (leadTrainEngine == null)
                        return;

                    // Don't automate a train if any of the train cars are player-owned.
                    // Not sure if this is the correct decision, but we'll see.
                    if (IsTrainOwned(trainCar))
                        return;

                    if (!ShouldAutomateTrain(trainCar, out var shouldCount))
                        return;

                    _trainManager.TryCreateTrainController(leadTrainEngine, TriggerData, countsTowardConductorLimit: shouldCount);
                    return;
                }

                // The PrimaryTrainCar always refers to the train car at the front of the direction being traveled.
                if (trainCar != trainCar.completeTrain.PrimaryTrainCar)
                    return;

                trainController.HandleTrigger(TriggerData);
            }
        }

        private class TriggerData
        {
            [JsonProperty("Id")]
            public int Id;

            [JsonProperty("Position")]
            public Vector3 Position;

            [JsonProperty("Enabled", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool Enabled = true;

            [JsonProperty("Route", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Route;

            [JsonProperty("TunnelType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string TunnelType;

            [JsonProperty("AddConductor", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool AddConductor;

            [JsonProperty("Brake", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Brake;

            [JsonProperty("Destroy", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Destroy;

            [JsonProperty("Direction", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Direction;

            [JsonProperty("Speed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Speed;

            [JsonProperty("TrackSelection", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string TrackSelection;

            [JsonProperty("StopDuration", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float StopDuration;

            [JsonProperty("DepartureSpeed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string DepartureSpeed;

            [JsonProperty("Commands", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<string> Commands;

            [JsonProperty("RotationAngle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float RotationAngle;

            [JsonProperty("TrainCars", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string[] TrainCars;

            [JsonProperty("Spawner", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private bool DeprecatedSpawner
            {
                set
                {
                    if (value && TrainCars == null)
                    {
                        TrainCars = new[] { TrainCarPrefab.WorkcartAlias };
                    }
                }
            }

            [JsonProperty("Wagons", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private string[] DeprecatedWagons
            {
                set
                {
                    if ((value?.Length ?? 0) == 0)
                        return;

                    TrainCars = TrainCars?.Concat(value).ToArray();
                }
            }

            [JsonIgnore]
            public bool IsSpawner => TrainCars?.Length > 0;

            [JsonIgnore]
            public TrainTriggerType TriggerType => TunnelType != null ? TrainTriggerType.Tunnel : TrainTriggerType.Map;

            public float GetStopDuration()
            {
                return StopDuration > 0
                    ? StopDuration
                    : 30;
            }

            private TunnelType? _tunnelType;
            public TunnelType GetTunnelType()
            {
                if (_tunnelType != null)
                    return (TunnelType)_tunnelType;

                _tunnelType = AutomatedWorkcarts.TunnelType.Unsupported;

                if (!string.IsNullOrWhiteSpace(TunnelType))
                {
                    if (Enum.TryParse(TunnelType, out TunnelType tunnelType))
                    {
                        _tunnelType = tunnelType;
                    }
                }

                return (TunnelType)_tunnelType;
            }

            private SpeedInstruction? _speedInstruction;
            public SpeedInstruction? GetSpeedInstruction()
            {
                if (_speedInstruction == null && !string.IsNullOrWhiteSpace(Speed))
                {
                    if (Enum.TryParse(Speed, out SpeedInstruction speed))
                    {
                        _speedInstruction = speed;
                    }
                }

                // Ensure there is a target speed when braking.
                return Brake ? _speedInstruction ?? SpeedInstruction.Zero : _speedInstruction;
            }

            public SpeedInstruction GetSpeedInstructionOrZero()
            {
                return GetSpeedInstruction() ?? SpeedInstruction.Zero;
            }

            private DirectionInstruction? _directionInstruction;
            public DirectionInstruction? GetDirectionInstruction()
            {
                if (_directionInstruction == null && !string.IsNullOrWhiteSpace(Direction))
                {
                    if (Enum.TryParse(Direction, out DirectionInstruction direction))
                    {
                        _directionInstruction = direction;
                    }
                }

                return _directionInstruction;
            }

            private TrackSelectionInstruction? _trackSelectionInstruction;
            public TrackSelectionInstruction? GetTrackSelectionInstruction()
            {
                if (_trackSelectionInstruction == null && !string.IsNullOrWhiteSpace(TrackSelection))
                {
                    if (Enum.TryParse(TrackSelection, out TrackSelectionInstruction trackSelection))
                    {
                        _trackSelectionInstruction = trackSelection;
                    }
                }

                return _trackSelectionInstruction;
            }

            private SpeedInstruction? _departureSpeedInstruction;
            public SpeedInstruction GetDepartureSpeedInstruction()
            {
                if (_departureSpeedInstruction == null && !string.IsNullOrWhiteSpace(Speed))
                {
                    if (Enum.TryParse(DepartureSpeed, out SpeedInstruction speed))
                    {
                        _departureSpeedInstruction = speed;
                    }
                }

                return _departureSpeedInstruction ?? SpeedInstruction.Med;
            }

            public bool MatchesRoute(string routeName)
            {
                if (string.IsNullOrWhiteSpace(Route))
                {
                    // Trigger has no specified route so it applies to all trains.
                    return true;
                }

                return routeName?.ToLower() == Route.ToLower();
            }

            public void InvalidateCache()
            {
                _speedInstruction = null;
                _directionInstruction = null;
                _trackSelectionInstruction = null;
                _departureSpeedInstruction = null;
            }

            public void CopyFrom(TriggerData triggerData)
            {
                Enabled = triggerData.Enabled;
                Route = triggerData.Route;
                AddConductor = triggerData.AddConductor;
                Brake = triggerData.Brake;
                Destroy = triggerData.Destroy;
                Speed = triggerData.Speed;
                DepartureSpeed = triggerData.DepartureSpeed;
                Direction = triggerData.Direction;
                TrackSelection = triggerData.TrackSelection;
                StopDuration = triggerData.StopDuration;
                TrainCars = triggerData.TrainCars;
                Commands = triggerData.Commands;
            }

            public TriggerData Clone()
            {
                var triggerData = new TriggerData();
                triggerData.CopyFrom(this);
                return triggerData;
            }

            public Color GetColor(string routeName)
            {
                if (!Enabled || !MatchesRoute(routeName))
                    return Color.grey;

                if (Destroy)
                    return Color.red;

                if (IsSpawner)
                    return new Color(0, 1, 0.75f);

                if (AddConductor)
                    return Color.cyan;

                var speedInstruction = GetSpeedInstruction();
                var directionInstruction = GetDirectionInstruction();
                var trackSelectionInstruction = GetTrackSelectionInstruction();

                float saturation;

                if (Brake)
                {
                    var brakeSpeedInstruction = GetSpeedInstructionOrZero();

                    // Orange
                    saturation = brakeSpeedInstruction == SpeedInstruction.Zero ? 1
                        : brakeSpeedInstruction == SpeedInstruction.Lo ? 0.8f
                        : 0.6f;
                    return Color.HSVToRGB(0.5f/6f, saturation, 1);
                }

                if (speedInstruction == SpeedInstruction.Zero)
                    return Color.white;

                if (speedInstruction == null && directionInstruction == null && trackSelectionInstruction != null)
                    return Color.magenta;

                var hue = directionInstruction == DirectionInstruction.Fwd
                    ? 1/3f // Green
                    : directionInstruction == DirectionInstruction.Rev
                        ? 0 // Red
                        : directionInstruction == DirectionInstruction.Invert
                            ? 0.5f/6f // Orange
                            : 1/6f; // Yellow

                saturation = speedInstruction == SpeedInstruction.Hi
                    ? 1
                    : speedInstruction == SpeedInstruction.Med
                    ? 0.8f
                    : speedInstruction == SpeedInstruction.Lo
                    ? 0.6f
                    : 1;

                return Color.HSVToRGB(hue, saturation, 1);
            }
        }

        #endregion

        #region Spawned Train Car Tracker

        private class SpawnedTrainCarTracker
        {
            private HashSet<TrainCar> _spawnedTrainCars = new();

            public bool ContainsTrainCar(TrainCar trainCar)
            {
                return _spawnedTrainCars.Contains(trainCar);
            }

            public void RegisterTrainCar(TrainCar trainCar)
            {
                _spawnedTrainCars.Add(trainCar);
            }

            public void UnregisterTrainCar(TrainCar trainCar)
            {
                _spawnedTrainCars.Remove(trainCar);
            }
        }

        private class SpawnedTrainCarComponent : FacepunchBehaviour
        {
            public static void AddToEntity(TrainCar trainCar, BaseTriggerInstance triggerInstance)
            {
                var component = trainCar.gameObject.AddComponent<SpawnedTrainCarComponent>();
                component._trainCar = trainCar;
                component._triggerInstance = triggerInstance;
                triggerInstance.TrainManager.SpawnedTrainCarTracker.RegisterTrainCar(trainCar);
            }

            private TrainCar _trainCar;
            private BaseTriggerInstance _triggerInstance;

            private void OnDestroy()
            {
                _triggerInstance.HandleTrainCarKilled(_trainCar);
                _triggerInstance.TrainManager.SpawnedTrainCarTracker.UnregisterTrainCar(_trainCar);
            }
        }

        #endregion

        #region Trigger Instances

        private abstract class BaseTriggerInstance
        {
            private const int MaxSpawnedTrains = 1;
            private const float TimeBetweenSpawns = 30;

            protected static readonly Vector3 TriggerOffset = new(0, 0.9f, 0);

            public TrainManager TrainManager { get; }
            public TriggerData TriggerData { get; }
            public TrainTrackSpline Spline { get; private set; }
            public float DistanceOnSpline { get; private set; }

            public abstract Vector3 WorldPosition { get; }
            protected abstract Quaternion WorldRotation { get; }

            public Vector3 TriggerPosition => WorldPosition + TriggerOffset;
            public Quaternion SpawnRotation =>
                Spline != null
                    ? GetSplineTangentRotation(Spline, DistanceOnSpline, WorldRotation)
                    : WorldRotation;

            private AutomatedWorkcarts _plugin;
            private GameObject _gameObject;
            private TrainTrigger _trainTrigger;
            private List<TrainCar> _spawnedTrains;

            protected BaseTriggerInstance(AutomatedWorkcarts plugin, TrainManager trainManager, TriggerData triggerData)
            {
                _plugin = plugin;
                TrainManager = trainManager;
                TriggerData = triggerData;
            }

            public void CreateTrigger()
            {
                _gameObject = new GameObject();
                OnMove();

                var sphereCollider = _gameObject.AddComponent<SphereCollider>();
                sphereCollider.isTrigger = true;
                sphereCollider.radius = TrainTrigger.TriggerRadius;
                sphereCollider.gameObject.layer = TrainTrigger.TriggerLayer;

                _trainTrigger = TrainTrigger.AddToGameObject(_plugin, _gameObject, TrainManager, TriggerData, this);

                if (TriggerData.IsSpawner)
                {
                    StartSpawningTrains();
                }
            }

            public void OnMove()
            {
                if (_gameObject == null)
                    return;

                _gameObject.transform.SetPositionAndRotation(TriggerPosition, WorldRotation);

                if (TryFindTrackNear(WorldPosition, 2, out var spline, out var distanceOnSpline))
                {
                    Spline = spline;
                    DistanceOnSpline = distanceOnSpline;
                }
                else
                {
                    Spline = null;
                    DistanceOnSpline = 0;
                }
            }

            public void OnRotate()
            {
                if (_gameObject == null)
                    return;

                _gameObject.transform.rotation = WorldRotation;
            }

            public void OnEnabledToggled()
            {
                if (TriggerData.Enabled)
                {
                    Enable();
                }
                else
                {
                    Disable();
                }
            }

            public void OnSpawnerToggled()
            {
                if (TriggerData.IsSpawner)
                {
                    if (TriggerData.Enabled)
                    {
                        StartSpawningTrains();
                    }
                }
                else
                {
                    KillTrains();
                    StopSpawningTrains();
                }
            }

            public void Respawn()
            {
                if (!TriggerData.IsSpawner || !TriggerData.Enabled)
                    return;

                KillTrains();
                SpawnTrain();
            }

            public void HandleTrainCarKilled(TrainCar trainCar)
            {
                _spawnedTrains.Remove(trainCar);
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(_gameObject);
                KillTrains();
            }

            public bool DidSpawnTrain(TrainCar trainCar)
            {
                return _spawnedTrains?.Contains(trainCar) ?? false;
            }

            private void Enable()
            {
                if (_gameObject != null)
                {
                    _gameObject.SetActive(true);
                    if (TriggerData.IsSpawner)
                    {
                        StartSpawningTrains();
                    }
                }
                else
                {
                    CreateTrigger();
                }
            }

            private void Disable()
            {
                if (_gameObject != null)
                {
                    _gameObject.SetActive(false);
                }

                KillTrains();
                StopSpawningTrains();
            }

            private void StartSpawningTrains()
            {
                _spawnedTrains ??= new List<TrainCar>(MaxSpawnedTrains);
                _trainTrigger.InvokeRepeating(SpawnTrainTracked, UnityEngine.Random.Range(0f, 1f), TimeBetweenSpawns);
            }

            private void StopSpawningTrains()
            {
                _trainTrigger.CancelInvoke(SpawnTrainTracked);
            }

            private void SpawnTrain()
            {
                if (_spawnedTrains.Count >= MaxSpawnedTrains)
                    return;

                if (Spline == null)
                    return;

                var trackSelection = ApplyTrackSelection(TrackSelection.Default, TriggerData.GetTrackSelectionInstruction());

                TrainCar previousTrainCar = null;
                TrainCarPrefab previousTrainCarPrefab = null;

                foreach (var trainCarAlias in TriggerData.TrainCars)
                {
                    var trainCarPrefab = TrainCarPrefab.FindPrefab(trainCarAlias);
                    if (trainCarPrefab == null)
                        continue;

                    if (previousTrainCar == null)
                    {
                        var worldPosition = WorldPosition;
                        var trainEnginePrefab = trainCarPrefab.PrefabPath;

                        if (trainCarPrefab.TrainCarAlias == TrainCarPrefab.WorkcartAlias && TriggerData.TrainCars.Length == 1)
                        {
                            var terrainHeight = TerrainMeta.HeightMap.GetHeight(worldPosition);
                            if (worldPosition.y - terrainHeight < -1)
                            {
                                trainEnginePrefab = TrainCarPrefab.ClassicWorkcartPrefab;
                            }
                        }

                        var rotation = SpawnRotation;
                        if (trainCarPrefab.Reverse)
                        {
                            rotation = Quaternion.LookRotation(rotation * -Vector3.forward);
                        }

                        var nextTrainCar = SpawnTrainCar(trainEnginePrefab, worldPosition, rotation);
                        if (nextTrainCar == null)
                            break;

                        previousTrainCar = nextTrainCar;
                    }
                    else
                    {
                        var nextTrainCar = AddTrainCar(previousTrainCar, previousTrainCarPrefab, trainCarPrefab, trackSelection);
                        if (nextTrainCar == null)
                            break;

                        previousTrainCar = nextTrainCar;
                    }

                    previousTrainCarPrefab = trainCarPrefab;

                    _spawnedTrains.Add(previousTrainCar);
                    SpawnedTrainCarComponent.AddToEntity(previousTrainCar, this);
                }

                if ((object)previousTrainCar != null)
                {
                    previousTrainCar.Invoke(() =>
                    {
                        var trainCars = previousTrainCar.completeTrain.trainCars;
                        for (var i = trainCars.Count - 1; i >= 0; i--)
                        {
                            trainCars[i].limitNetworking = false;
                        }
                    }, 0.1f);
                }
            }

            private void SpawnTrainTracked()
            {
                _plugin.TrackStart();
                SpawnTrain();
                _plugin.TrackEnd();
            }

            private void KillTrains()
            {
                if (_spawnedTrains == null)
                    return;

                for (var i = _spawnedTrains.Count - 1; i >= 0; i--)
                {
                    var trainCar = _spawnedTrains[i];
                    if (trainCar != null && !trainCar.IsDestroyed)
                    {
                        trainCar.Kill();
                    }

                    _spawnedTrains.RemoveAt(i);
                }
            }
        }

        private class MapTriggerInstance : BaseTriggerInstance
        {
            public override Vector3 WorldPosition => TriggerData.Position;
            protected override Quaternion WorldRotation => Quaternion.Euler(0, TriggerData.RotationAngle, 0);

            public MapTriggerInstance(AutomatedWorkcarts plugin, TrainManager trainManager, TriggerData triggerData)
                : base(plugin, trainManager, triggerData) {}
        }

        private class TunnelTriggerInstance : BaseTriggerInstance
        {
            public DungeonCellWrapper DungeonCellWrapper { get; }

            public override Vector3 WorldPosition => DungeonCellWrapper.TransformPoint(TriggerData.Position);
            protected override Quaternion WorldRotation => DungeonCellWrapper.Rotation * Quaternion.Euler(0, TriggerData.RotationAngle, 0);

            public TunnelTriggerInstance(AutomatedWorkcarts plugin, TrainManager trainManager, TriggerData triggerData, DungeonCellWrapper dungeonCellWrapper)
                : base(plugin, trainManager, triggerData)
            {
                DungeonCellWrapper = dungeonCellWrapper;
            }
        }

        #endregion

        #region Trigger Controllers

        private abstract class BaseTriggerController
        {
            protected TriggerData TriggerData { get; }
            public BaseTriggerInstance[] TriggerInstanceList { get; protected set; }

            protected TrainManager _trainManager;

            protected BaseTriggerController(TrainManager trainManager, TriggerData triggerData)
            {
                _trainManager = trainManager;
                TriggerData = triggerData;
            }

            public void OnMove()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnMove();
                }
            }

            public void OnRotate()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnRotate();
                }
            }

            public void OnEnabledToggled()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnEnabledToggled();
                }
            }

            public void OnSpawnerToggled()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.OnSpawnerToggled();
                }
            }

            public void Respawn()
            {
                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.Respawn();
                }
            }

            public void Destroy()
            {
                if (TriggerInstanceList == null)
                    return;

                foreach (var triggerInstance in TriggerInstanceList)
                {
                    triggerInstance.Destroy();
                }
            }

            public BaseTriggerInstance FindNearest(Vector3 position, float maxDistanceSquared, out float closestDistanceSquared)
            {
                BaseTriggerInstance closestTrigger = null;
                closestDistanceSquared = float.MaxValue;

                foreach (var triggerInstance in TriggerInstanceList)
                {
                    var distanceSquared = (position - triggerInstance.WorldPosition).sqrMagnitude;
                    if (distanceSquared < closestDistanceSquared && distanceSquared <= maxDistanceSquared)
                    {
                        closestTrigger = triggerInstance;
                        closestDistanceSquared = distanceSquared;
                    }
                }

                return closestTrigger;
            }
        }

        private sealed class MapTriggerController : BaseTriggerController
        {
            public MapTriggerController(TrainManager trainManager, TriggerData triggerData)
                : base(trainManager, triggerData) {}

            public void Create(AutomatedWorkcarts plugin)
            {
                var triggerInstance = new MapTriggerInstance(plugin, _trainManager, TriggerData);
                TriggerInstanceList = new BaseTriggerInstance[] { triggerInstance };

                if (TriggerData.Enabled)
                {
                    triggerInstance.CreateTrigger();
                }
            }
        }

        private sealed class TunnelTriggerController : BaseTriggerController
        {
            public TunnelTriggerController(TrainManager trainManager, TriggerData triggerData)
                : base(trainManager, triggerData) {}

            public void Create(AutomatedWorkcarts plugin)
            {
                var matchingDungeonCells = FindAllTunnelsOfType(TriggerData.GetTunnelType());
                TriggerInstanceList = new BaseTriggerInstance[matchingDungeonCells.Count];

                for (var i = 0; i < matchingDungeonCells.Count; i++)
                {
                    var triggerInstance = new TunnelTriggerInstance(plugin, _trainManager, TriggerData, matchingDungeonCells[i]);
                    TriggerInstanceList[i] = triggerInstance;

                    if (TriggerData.Enabled)
                    {
                        triggerInstance.CreateTrigger();
                    }
                }
            }
        }

        #endregion

        #region Trigger Manager

        private class TriggerManager
        {
            private class PlayerInfo
            {
                public Timer Timer;
                public string Route;
            }

            private const float TriggerDisplayDuration = 1f;
            private const float TriggerDisplayRadius = TrainTrigger.TriggerRadius;

            private AutomatedWorkcarts _plugin;
            private TrainManager _trainManager;
            private Dictionary<TriggerData, BaseTriggerController> _triggerControllers = new();
            private Dictionary<TrainTrackSpline, List<BaseTriggerInstance>> _splinesToTriggers = new();
            private Dictionary<ulong, PlayerInfo> _playerInfo = new();

            private Configuration _config => _plugin._config;
            private StoredTunnelData _tunnelData => _plugin._tunnelData;
            private StoredMapData _mapData => _plugin._mapData;
            private float TriggerDisplayDistanceSquared => _config.TriggerDisplayDistance * _config.TriggerDisplayDistance;

            public TriggerManager(AutomatedWorkcarts plugin, TrainManager trainManager)
            {
                _plugin = plugin;
                _trainManager = trainManager;
            }

            private void RegisterTriggerWithSpline(BaseTriggerInstance triggerInstance, TrainTrackSpline spline)
            {
                if (!_splinesToTriggers.TryGetValue(spline, out var triggerInstanceList))
                {
                    triggerInstanceList = new List<BaseTriggerInstance>() { triggerInstance };
                    _splinesToTriggers[spline] = triggerInstanceList;
                }
                else
                {
                    triggerInstanceList.Add(triggerInstance);
                }
            }

            private void UnregisterTriggerFromSpline(BaseTriggerInstance triggerInstance, TrainTrackSpline spline)
            {
                if (_splinesToTriggers.TryGetValue(spline, out var triggerInstanceList))
                {
                    triggerInstanceList.Remove(triggerInstance);
                    if (triggerInstanceList.Count == 0)
                    {
                        _splinesToTriggers.Remove(spline);
                    }
                }
            }

            private void RegisterTriggerWithSpline(BaseTriggerController triggerController)
            {
                foreach (var triggerInstance in triggerController.TriggerInstanceList)
                {
                    if (triggerInstance.Spline != null)
                    {
                        RegisterTriggerWithSpline(triggerInstance, triggerInstance.Spline);
                    }
                }
            }

            private void UnregisterTriggerFromSpline(BaseTriggerController triggerController)
            {
                foreach (var triggerInstance in triggerController.TriggerInstanceList)
                {
                    if (triggerInstance.Spline != null)
                    {
                        UnregisterTriggerFromSpline(triggerInstance, triggerInstance.Spline);
                    }
                }
            }

            public TriggerData FindTrigger(int triggerId, TrainTriggerType triggerType)
            {
                foreach (var triggerData in _triggerControllers.Keys)
                {
                    if (triggerData.TriggerType == triggerType && triggerData.Id == triggerId)
                        return triggerData;
                }

                return null;
            }

            public void AddTrigger(TriggerData triggerData)
            {
                if (triggerData.TriggerType == TrainTriggerType.Tunnel)
                {
                    if (triggerData.Id == 0)
                    {
                        triggerData.Id = GetNextTriggerId(_tunnelData.TunnelTriggers);
                    }

                    CreateTunnelTriggerController(triggerData);
                    _tunnelData.AddTrigger(triggerData);
                }
                else
                {
                    if (triggerData.Id == 0)
                    {
                        triggerData.Id = GetNextTriggerId(_mapData.MapTriggers);
                    }

                    CreateMapTriggerController(triggerData);
                    _mapData.AddTrigger(triggerData);
                }
            }

            private void SaveTrigger(TriggerData triggerData)
            {
                if (triggerData.TriggerType == TrainTriggerType.Tunnel)
                {
                    _tunnelData.Save();
                }
                else
                {
                    _mapData.Save();
                }
            }

            private BaseTriggerController GetTriggerController(TriggerData triggerData)
            {
                return _triggerControllers.TryGetValue(triggerData, out var triggerController)
                    ? triggerController
                    : null;
            }

            public void UpdateTrigger(TriggerData triggerData, TriggerData newTriggerData)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                var enabledChanged = triggerData.Enabled != newTriggerData.Enabled;
                var spawnerChanged = triggerData.IsSpawner != newTriggerData.IsSpawner;
                var trainCarsChanged = !CollectionsEqual(triggerData.TrainCars, newTriggerData.TrainCars);

                triggerData.CopyFrom(newTriggerData);
                triggerData.InvalidateCache();

                if (enabledChanged)
                {
                    triggerController.OnEnabledToggled();

                    if (triggerData.Enabled)
                    {
                        RegisterTriggerWithSpline(triggerController);
                    }
                    else
                    {
                        UnregisterTriggerFromSpline(triggerController);
                    }
                }

                if (spawnerChanged)
                {
                    triggerController.OnSpawnerToggled();
                }
                else if (trainCarsChanged)
                {
                    triggerController.Respawn();
                }

                SaveTrigger(triggerData);
            }

            public void MoveTrigger(TriggerData triggerData, Vector3 position)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                UnregisterTriggerFromSpline(triggerController);
                triggerData.Position = position;
                triggerController.OnMove();
                RegisterTriggerWithSpline(triggerController);
                SaveTrigger(triggerData);
            }

            public void RotateTrigger(TriggerData triggerData, float rotationAngle)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                triggerData.RotationAngle = rotationAngle;
                triggerController.OnRotate();
                SaveTrigger(triggerData);
            }

            public void RespawnTrigger(TriggerData triggerData)
            {
                GetTriggerController(triggerData)?.Respawn();
            }

            public void AddTriggerCommand(TriggerData triggerData, string command)
            {
                triggerData.Commands ??= new List<string>();

                if (triggerData.Commands.Contains(command, StringComparer.InvariantCultureIgnoreCase))
                    return;

                triggerData.Commands.Add(command);
                SaveTrigger(triggerData);
            }

            public void RemoveTriggerCommand(TriggerData triggerData, int index)
            {
                triggerData.Commands.RemoveAt(index);
                SaveTrigger(triggerData);
            }

            private void DestroyTriggerController(BaseTriggerController triggerController)
            {
                UnregisterTriggerFromSpline(triggerController);
                triggerController.Destroy();
            }

            public void RemoveTrigger(TriggerData triggerData)
            {
                var triggerController = GetTriggerController(triggerData);
                if (triggerController == null)
                    return;

                DestroyTriggerController(triggerController);
                _triggerControllers.Remove(triggerData);

                if (triggerData.TriggerType == TrainTriggerType.Tunnel)
                {
                    _tunnelData.RemoveTrigger(triggerData);
                }
                else
                {
                    _mapData.RemoveTrigger(triggerData);
                }
            }

            private void CreateMapTriggerController(TriggerData triggerData)
            {
                var triggerController = new MapTriggerController(_trainManager, triggerData);
                triggerController.Create(_plugin);
                RegisterTriggerWithSpline(triggerController);
                _triggerControllers[triggerData] = triggerController;
            }

            private void CreateTunnelTriggerController(TriggerData triggerData)
            {
                var triggerController = new TunnelTriggerController(_trainManager, triggerData);
                triggerController.Create(_plugin);
                RegisterTriggerWithSpline(triggerController);
                _triggerControllers[triggerData] = triggerController;
            }

            public IEnumerator CreateAll()
            {
                if (_config.EnableMapTriggers)
                {
                    foreach (var triggerData in _mapData.MapTriggers)
                    {
                        CreateMapTriggerController(triggerData);
                        _plugin.TrackEnd();
                        yield return CoroutineEx.waitForEndOfFrame;
                        _plugin.TrackStart();
                    }
                }

                foreach (var triggerData in _tunnelData.TunnelTriggers)
                {
                    var tunnelType = triggerData.GetTunnelType();
                    if (tunnelType == TunnelType.Unsupported || !_config.IsTunnelTypeEnabled(tunnelType))
                        continue;

                    CreateTunnelTriggerController(triggerData);
                    _plugin.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                    _plugin.TrackStart();
                }
            }

            public void DestroyAll()
            {
                foreach (var triggerController in _triggerControllers.Values)
                {
                    DestroyTriggerController(triggerController);
                }

                _triggerControllers.Clear();
                _splinesToTriggers.Clear();
            }

            private PlayerInfo GetOrCreatePlayerInfo(BasePlayer player)
            {
                if (!_playerInfo.TryGetValue(player.userID, out var playerInfo))
                {
                    playerInfo = new PlayerInfo();
                    _playerInfo[player.userID] = playerInfo;
                }

                return playerInfo;
            }

            public void SetPlayerDisplayedRoute(BasePlayer player, string routeName)
            {
                GetOrCreatePlayerInfo(player).Route = routeName;
            }

            public void ShowAllRepeatedly(BasePlayer player, int duration = -1)
            {
                // Some commands can be run from the server, in which case the BasePlayer will be null.
                if (player == null)
                    return;

                var playerInfo = GetOrCreatePlayerInfo(player);

                ShowNearbyTriggers(player, player.transform.position, playerInfo.Route);

                if (playerInfo.Timer is { Destroyed: false })
                {
                    var newDuration = duration >= 0 ? duration : Math.Max(playerInfo.Timer.Repetitions, 60);
                    playerInfo.Timer.Reset(delay: -1, repetitions: newDuration);
                    return;
                }

                if (duration == -1)
                    duration = 60;

                playerInfo.Timer = _plugin.timer.Repeat(TriggerDisplayDuration - 0.2f, duration, () =>
                {
                    ShowNearbyTriggers(player, player.transform.position, playerInfo.Route);
                });
            }

            private void ShowNearbyTriggers(BasePlayer player, Vector3 playerPosition, string routeName)
            {
                var isAdmin = player.IsAdmin;
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var triggerController in _triggerControllers.Values)
                {
                    foreach (var triggerInstance in triggerController.TriggerInstanceList)
                    {
                        if ((playerPosition - triggerInstance.WorldPosition).sqrMagnitude <= TriggerDisplayDistanceSquared)
                        {
                            ShowTrigger(player, triggerInstance, routeName, triggerController.TriggerInstanceList.Length);
                        }
                    }
                }

                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }

            private void ShowTrigger(BasePlayer player, BaseTriggerInstance trigger, string routeName, int count = 1)
            {
                var triggerData = trigger.TriggerData;
                var color = triggerData.GetColor(routeName);

                var spherePosition = trigger.TriggerPosition;
                player.SendConsoleCommand("ddraw.sphere", TriggerDisplayDuration, color, spherePosition, TriggerDisplayRadius);

                var triggerPrefix = _plugin.GetTriggerPrefix(player, triggerData);
                var infoLines = new List<string>();

                if (!triggerData.Enabled)
                {
                    infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerDisabled));
                }

                infoLines.Add(_plugin.GetMessage(player, Lang.InfoTrigger, triggerPrefix, triggerData.Id));

                if (triggerData.TriggerType == TrainTriggerType.Tunnel)
                {
                    infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerTunnel, triggerData.TunnelType, count));
                }
                else
                {
                    infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerMap, triggerData.Id));
                }

                if (!string.IsNullOrWhiteSpace(triggerData.Route))
                {
                    infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerRoute, triggerData.Route));
                }

                if (triggerData.Destroy)
                {
                    infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerDestroy));
                }
                else
                {
                    if (triggerData.IsSpawner)
                    {
                        var sb = new StringBuilder();
                        if (triggerData.TrainCars.Length > 10)
                        {
                            sb.AppendLine();
                        }

                        for (var i = 0; i < triggerData.TrainCars.Length; i++)
                        {
                            var trainCarPrefab = TrainCarPrefab.FindPrefab(triggerData.TrainCars[i]);
                            if (trainCarPrefab != null)
                            {
                                // Show at most 10 train cars per line.
                                if (i > 0 && i % 10 == 0)
                                {
                                    sb.AppendLine();
                                }
                                else
                                {
                                    sb.Append(" ");
                                    sb.Append(trainCarPrefab.TrainCarAlias);
                                }
                            }
                        }

                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerSpawner, sb.ToString()));

                        var spawnRotation = trigger.SpawnRotation;
                        var arrowBack = spherePosition + Vector3.up + spawnRotation * Vector3.back * 1.5f;
                        var arrowForward = spherePosition + Vector3.up + spawnRotation * Vector3.forward * 1.5f;
                        player.SendConsoleCommand("ddraw.arrow", TriggerDisplayDuration, color, arrowBack, arrowForward, 0.5f);
                    }

                    if (triggerData.AddConductor)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerAddConductor));
                    }

                    var directionInstruction = triggerData.GetDirectionInstruction();
                    var speedInstruction = triggerData.GetSpeedInstruction();

                    // When speed is zero, departure direction will be shown instead of direction.
                    if (directionInstruction != null && speedInstruction != SpeedInstruction.Zero)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerDirection, directionInstruction));
                    }

                    if (triggerData.Brake)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerBrakeToSpeed, triggerData.GetSpeedInstructionOrZero()));
                    }
                    else if (speedInstruction != null)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerSpeed, speedInstruction));
                    }

                    if (speedInstruction == SpeedInstruction.Zero)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerStopDuration, triggerData.GetStopDuration()));
                    }

                    var trackSelectionInstruction = triggerData.GetTrackSelectionInstruction();
                    if (trackSelectionInstruction != null)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerTrackSelection, trackSelectionInstruction));
                    }

                    if (directionInstruction != null && speedInstruction == SpeedInstruction.Zero)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerDepartureDirection, directionInstruction));
                    }

                    var departureSpeedInstruction = triggerData.GetDepartureSpeedInstruction();
                    if (speedInstruction == SpeedInstruction.Zero)
                    {
                        infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerDepartureSpeed, departureSpeedInstruction));
                    }
                }

                if (triggerData.Commands is { Count: > 0 })
                {
                    var commandList = "";
                    for (var i = 0; i < triggerData.Commands.Count; i++)
                    {
                        commandList += $"\n({i+1}): {triggerData.Commands[i]}";
                    }

                    infoLines.Add(_plugin.GetMessage(player, Lang.InfoTriggerCommands, commandList));
                }

                var textPosition = trigger.TriggerPosition + new Vector3(0, 1.5f + infoLines.Count * 0.075f, 0);
                player.SendConsoleCommand("ddraw.text", TriggerDisplayDuration, color, textPosition, string.Join("\n", infoLines));
            }

            public BaseTriggerInstance FindNearestTrigger(Vector3 position, float maxDistanceSquared = 9)
            {
                BaseTriggerInstance closestTriggerInstance = null;
                var closestDistanceSquared = float.MaxValue;

                foreach (var triggerController in _triggerControllers.Values)
                {
                    var triggerInstance = triggerController.FindNearest(position, maxDistanceSquared, out var distanceSquared);

                    if (distanceSquared < closestDistanceSquared && distanceSquared <= maxDistanceSquared)
                    {
                        closestTriggerInstance = triggerInstance;
                        closestDistanceSquared = distanceSquared;
                    }
                }

                return closestTriggerInstance;
            }

            public BaseTriggerInstance FindNearestTrigger(Vector3 position, TriggerData triggerData, float maxDistanceSquared = float.MaxValue)
            {
                return GetTriggerController(triggerData)?.FindNearest(position, maxDistanceSquared, out _);
            }

            public TriggerData FindNearestTriggerWhereAiming(BasePlayer player, float maxDistanceSquared = 9)
            {
                var triggerInstance = GetHitTrigger(player);
                if (triggerInstance != null)
                    return triggerInstance.TriggerData;

                if (!TryGetTrackPosition(player, out var trackPosition))
                    return null;

                return FindNearestTrigger(trackPosition, maxDistanceSquared)?.TriggerData;
            }
        }

        #endregion

        #region Train Manager

        private class TrainManager
        {
            public SpawnedTrainCarTracker SpawnedTrainCarTracker { get; }

            private AutomatedWorkcarts _plugin;
            private HashSet<TrainController> _trainControllers = new();
            private Dictionary<TrainCar, ITrainCarComponent> _trainCarComponents = new();
            private bool _isUnloading;

            public int TrainCount => _trainControllers.Count;

            public int CountedConductors
            {
                get
                {
                    var count = 0;

                    foreach (var trainController in _trainControllers)
                    {
                        if (trainController.CountsTowardConductorLimit)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }

            private Configuration _config => _plugin._config;
            private StoredPluginData _data => _plugin._data;

            public TrainEngine[] GetAutomatedTrainEngines()
            {
                var trainEngineList = new List<TrainEngine>();

                foreach (var trainController in _trainControllers)
                {
                    trainController.GetTrainEngines(trainEngineList);
                }

                return trainEngineList.ToArray();
            }

            public TrainManager(AutomatedWorkcarts plugin, SpawnedTrainCarTracker spawnedTrainCarTracker)
            {
                _plugin = plugin;
                SpawnedTrainCarTracker = spawnedTrainCarTracker;
            }

            public bool CanHaveMoreConductors()
            {
                if (_config.MaxConductors < 0)
                    return true;

                return CountedConductors < _config.MaxConductors;
            }

            public TrainController GetTrainController(TrainCar trainCar)
            {
                return _trainCarComponents.TryGetValue(trainCar, out var trainCarComponent)
                    ? trainCarComponent.TrainController
                    : null;
            }

            public bool HasTrainController(TrainCar trainCar)
            {
                return GetTrainController(trainCar) != null;
            }

            public bool TryCreateTrainController(TrainEngine primaryTrainEngine, TriggerData triggerData = null, TrainEngineData trainEngineData = null, bool countsTowardConductorLimit = true)
            {
                foreach (var trainCar in primaryTrainEngine.completeTrain.trainCars)
                {
                    if (_trainCarComponents.ContainsKey(trainCar))
                        return false;

                    var trainEngine = trainCar as TrainEngine;
                    if ((object)trainEngine != null && _plugin.AutomationWasBlocked(trainEngine))
                        return false;
                }

                trainEngineData ??= new TrainEngineData
                {
                    Route = triggerData?.Route,
                };

                var trainController = new TrainController(_plugin, this, trainEngineData, countsTowardConductorLimit);
                _trainControllers.Add(trainController);

                var primaryTrainEngineController = TrainEngineController.AddToEntity(_plugin, primaryTrainEngine, trainController);
                trainController.AddTrainCarComponent(primaryTrainEngineController);
                _trainCarComponents[primaryTrainEngine] = primaryTrainEngineController;

                if (!SpawnedTrainCarTracker.ContainsTrainCar(primaryTrainEngine))
                {
                    _data.AddTrainEngineId(primaryTrainEngine.net.ID.Value, trainEngineData);
                }

                var primaryForward = primaryTrainEngine.transform.forward;

                foreach (var trainCar in primaryTrainEngine.completeTrain.trainCars)
                {
                    var trainEngine = trainCar as TrainEngine;
                    if ((object)trainEngine != null)
                    {
                        if (trainEngine == primaryTrainEngine)
                            continue;

                        // This approach will need to be updated if people have long trains and/or tight corners.
                        var isReverse = Vector3.Dot(primaryForward, trainEngine.transform.forward) < 0;

                        var trainEngineController = TrainEngineController.AddToEntity(_plugin, trainEngine, trainController, isReverse);
                        _trainCarComponents[trainEngine] = trainEngineController;
                        trainController.AddTrainCarComponent(trainEngineController);
                    }
                    else
                    {
                        var trainCarComponent = TrainCarComponent.AddToEntity(trainCar, trainController);
                        _trainCarComponents[trainCar] = trainCarComponent;
                        trainController.AddTrainCarComponent(trainCarComponent);
                    }

                }

                trainController.StartTrain();

                if (triggerData != null)
                {
                    trainController.HandleConductorTrigger(triggerData);
                }

                return true;
            }

            public void UnregisterTrainCarComponent(ITrainCarComponent trainCarComponent)
            {
                _trainCarComponents.Remove(trainCarComponent.TrainCar);

                if (!_isUnloading)
                {
                    var trainEngineController = trainCarComponent as TrainEngineController;
                    if ((object)trainEngineController != null)
                    {
                        _data.RemoveTrainEngineId(trainEngineController.NetId);
                    }
                }
            }

            public void UnregisterTrainController(TrainController trainController)
            {
                _trainControllers.Remove(trainController);
            }

            public void KillTrainController(TrainCar trainCar)
            {
                GetTrainController(trainCar)?.Kill();
            }

            public int ResetAll()
            {
                var trainCount = TrainCount;

                foreach (var trainController in _trainControllers.ToArray())
                {
                    // Don't reset conductors that are on spawned by conductor triggers.
                    if (!trainController.CountsTowardConductorLimit)
                        continue;

                    trainController.Kill();
                }

                return trainCount;
            }

            public void Unload()
            {
                _isUnloading = true;

                ResetAll();
            }

            public void ResendAllGenericMarkers()
            {
                foreach (var trainController in _trainControllers)
                {
                    trainController?.ResendGenericMarker();
                }
            }

            public bool UpdateTrainEngineData()
            {
                var changed = false;

                foreach (var trainController in _trainControllers)
                {
                    if (trainController.UpdateTrainEngineData())
                    {
                        changed = true;
                    }
                }

                return changed;
            }
        }

        #endregion

        #region Train State

        private abstract class TrainState
        {
            protected TrainController _trainController;

            public abstract void Enter();
            public abstract void Exit();

            protected TrainState(TrainController trainController)
            {
                _trainController = trainController;
            }
        }

        private class DrivingState : TrainState
        {
            public EngineSpeeds Throttle;

            public DrivingState(TrainController trainController, EngineSpeeds throttle) : base(trainController)
            {
                Throttle = throttle;
            }

            public override void Enter()
            {
                _trainController.SetThrottle(Throttle);
            }

            public override void Exit() {}
        }

        private abstract class TransitionState : TrainState
        {
            public readonly TrainState NextState;

            protected TransitionState(TrainController trainController, TrainState nextState) : base(trainController)
            {
                _trainController = trainController;
                NextState = nextState;
            }

            public T GetNextStateOfType<T>() where T : TrainState
            {
                return NextState as T ?? (NextState as TransitionState)?.GetNextStateOfType<T>();
            }

            public void SwitchToNextStateOfType<T>() where T : TrainState
            {
                _trainController.SwitchState(GetNextStateOfType<T>());
            }

            protected void SwitchToNextState()
            {
                _trainController.SwitchState(NextState);
            }
        }

        private class BrakingState : TransitionState
        {
            public EngineSpeeds TargetThrottle;
            public bool IsStopping => TargetThrottle == EngineSpeeds.Zero;

            public BrakingState(TrainController trainController, TrainState nextState, EngineSpeeds targetThrottle)
                : base(trainController, nextState)
            {
                TargetThrottle = targetThrottle;
            }

            public override void Enter()
            {
                var brakeThrottle = ApplySpeedAndDirection(_trainController.DepartureThrottle, SpeedInstruction.Lo, DirectionInstruction.Invert);
                _trainController.SetThrottle(brakeThrottle);
                _trainController.PrimaryTrainEngineController.InvokeRepeatingFixedTime(BrakeUpdate);
            }

            public override void Exit()
            {
                _trainController.PrimaryTrainEngineController.CancelInvokeFixedTime(BrakeUpdate);
            }

            private bool IsNearSpeed(EngineSpeeds desiredThrottle, float leeway = 0.1f)
            {
                var trainEngine = _trainController.PrimaryTrainEngine;

                var currentSpeed = Vector3.Dot(_trainController.PrimaryTrainEngineController.Transform.forward, trainEngine.GetLocalVelocity());
                var desiredSpeed = trainEngine.maxSpeed * GetThrottleFraction(desiredThrottle);

                // If desiring negative speed, current speed is expected to increase while braking (e.g., -10 to -5).
                // If desiring positive speed, current speed is expected to decrease while braking (e.g., 10 to 5).
                // If desiring zero speed, the direction depends on the throttle being applied (e.g., if positive, -10 to -5).
                return desiredSpeed < 0 || (desiredSpeed == 0 && GetThrottleFraction(trainEngine.CurThrottleSetting) > 0)
                    ? currentSpeed + leeway >= desiredSpeed
                    : currentSpeed - leeway <= desiredSpeed;
            }

            private void BrakeUpdate()
            {
                if (IsNearSpeed(TargetThrottle))
                {
                    SwitchToNextState();
                }
            }
        }

        private class IdleState : TransitionState
        {
            private readonly float _idleSeconds;
            private readonly bool _reducePressureOnDeparture;

            public IdleState(TrainController trainController, TrainState nextState, float idleSeconds, bool reducePressureOnDeparture = true)
                : base(trainController, nextState)
            {
                _idleSeconds = idleSeconds;
                _reducePressureOnDeparture = reducePressureOnDeparture;
            }

            public override void Enter()
            {
                _trainController.SetThrottle(EngineSpeeds.Zero);
                _trainController.PrimaryTrainEngineController.Invoke(StopIdling, _idleSeconds);
            }

            public override void Exit()
            {
                _trainController.PrimaryTrainEngineController.CancelInvoke(StopIdling);
            }

            private void StopIdling()
            {
                if (_reducePressureOnDeparture)
                {
                    _trainController.ReducePressure();
                }

                SwitchToNextState();
            }
        }

        #endregion

        #region Train Controller

        private class TrainController
        {
            private const float MaxBackPressure = 15f;
            private const float BackPressureIncrement = 3f;

            public TrainManager TrainManager { get; }
            public TrainEngineController PrimaryTrainEngineController { get; private set; }
            public bool IsDestroying { get; private set; }
            public bool CountsTowardConductorLimit { get; }
            public TrainEngine PrimaryTrainEngine => PrimaryTrainEngineController.TrainEngine;
            private Configuration _config => _plugin._config;

            public Vector3 Forward => EngineThrottleToNumber(DepartureThrottle) >= 0
                ? PrimaryTrainEngine.transform.forward
                : -PrimaryTrainEngine.transform.forward;

            private bool IsStopped => _trainState is IdleState;
            private bool IsStopping => (_trainState as BrakingState)?.IsStopping ?? false;

            private SpawnedTrainCarTracker _spawnedTrainCarTracker => TrainManager.SpawnedTrainCarTracker;

            private DrivingState _nextDrivingState =>
                _trainState as DrivingState ?? (_trainState as TransitionState)?.GetNextStateOfType<DrivingState>();

            private AutomatedWorkcarts _plugin;
            private readonly List<TrainEngineController> _trainEngineControllers = new();
            private readonly List<ITrainCarComponent> _trainCarComponents = new();

            private TrainState _trainState;
            private TrainEngineData _trainEngineData;

            private Func<BasePlayer, bool> _nearbyPlayerFilter;
            private TrainCollisionTrigger _collisionTriggerA;
            private TrainCollisionTrigger _collisionTriggerB;
            private MapMarkerGenericRadius _genericMarker;
            private VendingMachineMapMarker _vendingMarker;
            private float _backPressure;
            private bool _isDestroyed;

            // Desired velocity, ignoring circumstances like stopping/braking/chilling.
            public EngineSpeeds DepartureThrottle =>
                _nextDrivingState?.Throttle ?? PrimaryTrainEngine.CurThrottleSetting;

            public void ScheduleCinematicDestruction()
            {
                IsDestroying = true;
                PrimaryTrainEngineController.Invoke(DestroyCinematically, 0);
            }

            public TrainController(AutomatedWorkcarts plugin, TrainManager trainManager, TrainEngineData workcartData, bool countsTowardConductorLimit)
            {
                _plugin = plugin;
                TrainManager = trainManager;
                _trainEngineData = workcartData;
                _nearbyPlayerFilter = NearbyPlayerFilter;
                CountsTowardConductorLimit = countsTowardConductorLimit;
            }

            public void AddTrainCarComponent(ITrainCarComponent trainCarComponent)
            {
                _trainCarComponents.Add(trainCarComponent);

                var trainEngineController = trainCarComponent as TrainEngineController;
                if ((object)trainEngineController != null)
                {
                    _trainEngineControllers.Add(trainEngineController);

                    if ((object)PrimaryTrainEngineController == null)
                    {
                        PrimaryTrainEngineController = trainEngineController;
                    }
                }
            }

            public void HandleTrainCarDestroyed(ITrainCarComponent trainCarComponent)
            {
                _trainCarComponents.Remove(trainCarComponent);
                TrainManager.UnregisterTrainCarComponent(trainCarComponent);

                // Any train car removal should disable automation of the entire train.
                Kill();
            }

            public void GetTrainEngines(List<TrainEngine> trainEngineList)
            {
                foreach (var trainEngineController in _trainEngineControllers)
                {
                    trainEngineList.Add(trainEngineController.TrainEngine);
                }
            }

            public void StartTrain()
            {
                MaybeAddMapMarkers();
                SetupCollisionTriggers();

                DisableTrainCoupling(PrimaryTrainEngine.completeTrain);
                EnableInvincibility();

                var throttle = _trainEngineData.Throttle ?? EngineSpeeds.Zero;
                if (throttle == EngineSpeeds.Zero)
                {
                    throttle = _config.GetDefaultSpeed();
                }

                SwitchState(new DrivingState(this, throttle));
                SetTrackSelection(_trainEngineData.TrackSelection ?? _config.GetDefaultTrackSelection());

                if (_config.PlayHornForNearbyPlayersInRadius > 0)
                {
                    PrimaryTrainEngineController.InvokeRandomized(MaybeToggleHorn, 1f, 1f, 0.15f);
                }
            }

            public void SetThrottle(EngineSpeeds throttle)
            {
                foreach (var trainEngineController in _trainEngineControllers)
                {
                    trainEngineController.SetThrottle(throttle);
                }
            }

            public void SetTrackSelection(TrackSelection trackSelection)
            {
                foreach (var trainEngineController in _trainEngineControllers)
                {
                    trainEngineController.SetTrackSelection(trackSelection);
                }
            }

            public void HandleTrigger(TriggerData triggerData)
            {
                if (!triggerData.MatchesRoute(_trainEngineData.Route))
                    return;

                if (triggerData.Commands is { Count: > 0 })
                {
                    foreach (var command in triggerData.Commands)
                    {
                        var fullCommand = IdRegex.Replace(command, PrimaryTrainEngineController.NetIdString);
                        if (!string.IsNullOrWhiteSpace(fullCommand))
                        {
                            _plugin.server.Command(fullCommand);
                        }
                    }
                }

                if (triggerData.Destroy)
                {
                    PrimaryTrainEngineController.Invoke(() =>
                    {
                        foreach (var trainCarComponent in _trainCarComponents.ToArray())
                        {
                            if (trainCarComponent.TrainCar != null && !trainCarComponent.TrainCar.IsDestroyed)
                            {
                                trainCarComponent.TrainCar.Kill(BaseNetworkable.DestroyMode.Gib);
                            }
                        }
                    }, 0);

                    return;
                }

                SetTrackSelection(
                    ApplyTrackSelection(PrimaryTrainEngine.localTrackSelection, triggerData.GetTrackSelectionInstruction())
                );

                var directionInstruction = triggerData.GetDirectionInstruction();
                var departureSpeedInstruction = triggerData.GetDepartureSpeedInstruction();

                var currentDepartureThrottle = DepartureThrottle;
                var newDepartureThrottle = ApplySpeedAndDirection(currentDepartureThrottle, departureSpeedInstruction, directionInstruction);

                if (triggerData.Brake)
                {
                    var brakeSpeedInstruction = triggerData.GetSpeedInstructionOrZero();
                    if (brakeSpeedInstruction == SpeedInstruction.Zero)
                    {
                        var finalState = new DrivingState(this, newDepartureThrottle);
                        var nextState = new IdleState(this, finalState, triggerData.GetStopDuration());
                        SwitchState(new BrakingState(this, nextState, EngineSpeeds.Zero));
                        return;
                    }

                    var brakeUntilVelocity = ApplySpeedAndDirection(currentDepartureThrottle, brakeSpeedInstruction, directionInstruction);
                    SwitchState(new BrakingState(this, new DrivingState(this, brakeUntilVelocity), brakeUntilVelocity));
                    return;
                }

                var speedInstruction = triggerData.GetSpeedInstruction();
                if (speedInstruction == SpeedInstruction.Zero)
                {
                    if (_trainState is BrakingState brakingState)
                    {
                        // Update brake-to speed.
                        brakingState.TargetThrottle = EngineSpeeds.Zero;
                        return;
                    }

                    // Trigger with speed Zero, but no braking.
                    SwitchState(new IdleState(this, new DrivingState(this, newDepartureThrottle), triggerData.GetStopDuration()));
                    return;
                }

                var nextThrottle = ApplySpeedAndDirection(currentDepartureThrottle, speedInstruction, directionInstruction);

                if (_trainState is DrivingState drivingState)
                {
                    drivingState.Throttle = nextThrottle;
                }
                else
                {
                    SwitchState(new DrivingState(this, nextThrottle));
                }

                SetThrottle(nextThrottle);
            }

            public void ResendGenericMarker()
            {
                if (_genericMarker != null)
                {
                    _genericMarker.SendUpdate();
                }
            }

            public void StartChilling(TrainController forwardController = null)
            {
                if (_trainState is IdleState)
                    return;

                _backPressure = Math.Min((forwardController?._backPressure ?? 0) + BackPressureIncrement, MaxBackPressure);
                SwitchState(new IdleState(this, _trainState, _backPressure, reducePressureOnDeparture: false));
            }

            public void ReducePressure()
            {
                _backPressure = Math.Max(_backPressure - BackPressureIncrement, 0);
            }

            public void DepartEarlyIfStoppedOrStopping()
            {
                (_trainState as TransitionState)?.SwitchToNextStateOfType<DrivingState>();
            }

            public void SwitchState(TrainState nextState)
            {
                _trainState?.Exit();
                _trainState = nextState;
                nextState?.Enter();
            }

            public void HandleConductorTrigger(TriggerData triggerData)
            {
                SetThrottle(EngineSpeeds.Zero);

                // Delay at least one second in case the train engine needs to reposition after spawning.
                // Not delaying may cause the train engine to get stuck for unknown reasons.
                // Delay a random interval to spread out load.
                PrimaryTrainEngineController.Invoke(() =>
                {
                    HandleTrigger(triggerData);
                }, UnityEngine.Random.Range(1, 2f));
            }

            public bool UpdateTrainEngineData()
            {
                if (_spawnedTrainCarTracker.ContainsTrainCar(PrimaryTrainEngine))
                    return false;

                return _trainEngineData.UpdateData(DepartureThrottle, PrimaryTrainEngine.localTrackSelection);
            }

            public void Kill()
            {
                if (_isDestroyed)
                    return;

                _isDestroyed = true;

                UnityEngine.Object.DestroyImmediate(_collisionTriggerA);
                UnityEngine.Object.DestroyImmediate(_collisionTriggerB);

                DisableInvincibility();

                foreach (var trainCarComponent in _trainCarComponents)
                {
                    UpdateAllowedCouplings(trainCarComponent.TrainCar, allowFront: true, allowRear: true);
                }

                if (_genericMarker != null && !_genericMarker.IsDestroyed)
                {
                    _genericMarker.Kill();
                }

                if (_vendingMarker != null && !_vendingMarker.IsDestroyed)
                {
                    _vendingMarker.Kill();
                }

                for (var i = _trainCarComponents.Count - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(_trainCarComponents[i] as FacepunchBehaviour);
                }

                TrainManager.UnregisterTrainController(this);
            }

            private bool IsPlayerOnboardTrain(BasePlayer player)
            {
                var trainCar = player.GetParentEntity() as TrainCar
                    ?? player.GetMountedVehicle() as TrainCar;

                if ((object)trainCar == null)
                    return false;

                return TrainManager.GetTrainController(trainCar) == this;
            }

            private bool NearbyPlayerFilter(BasePlayer player)
            {
                if (player.IsDestroyed || !player.IsConnected || !player.userID.IsSteamId() || player.IsDead() || player.IsSleeping() || player.IsSpectating())
                    return false;

                if (IsPlayerOnboardTrain(player))
                    return false;

                return true;
            }

            private bool ShouldPlayHorn()
            {
                if (IsStopped || IsStopping)
                    return false;

                return Query.Server.GetPlayersInSphere(
                    PrimaryTrainEngineController.Position,
                    _config.PlayHornForNearbyPlayersInRadius,
                    _plugin._playerQueryResults,
                    _nearbyPlayerFilter
                ) > 0;
            }

            private void MaybeToggleHorn()
            {
                _plugin.TrackStart();
                PrimaryTrainEngine.SetFlag(Flag_Horn, ShouldPlayHorn());
                _plugin.TrackEnd();
            }

            private void MaybeAddMapMarkers()
            {
                if (_config.GenericMapMarker.Enabled)
                {
                    _genericMarker = GameManager.server.CreateEntity(GenericMapMarkerPrefab, PrimaryTrainEngineController.Position) as MapMarkerGenericRadius;
                    if (_genericMarker != null)
                    {
                        _genericMarker.EnableSaving(false);
                        _genericMarker.EnableGlobalBroadcast(true);
                        _genericMarker.syncPosition = false;
                        _genericMarker.Spawn();

                        _genericMarker.color1 = _config.GenericMapMarker.GetColor();
                        _genericMarker.color2 = _genericMarker.color1;
                        _genericMarker.alpha = _config.GenericMapMarker.Alpha;
                        _genericMarker.radius = _config.GenericMapMarker.Radius;
                        _genericMarker.SendUpdate();
                    }
                }

                if (_config.VendingMapMarker.Enabled)
                {
                    _vendingMarker = GameManager.server.CreateEntity(VendingMapMarkerPrefab, PrimaryTrainEngineController.Position) as VendingMachineMapMarker;
                    if (_vendingMarker != null)
                    {
                        _vendingMarker.markerShopName = _config.VendingMapMarker.Name;

                        _vendingMarker.EnableSaving(false);
                        _vendingMarker.EnableGlobalBroadcast(true);
                        _vendingMarker.syncPosition = false;
                        _vendingMarker.Spawn();
                    }
                }

                if (_genericMarker == null && _vendingMarker == null)
                    return;

                // Periodically update the marker positions since they aren't parented to the train engines.
                // We could parent them to the train engines, but then they would only appear to players in network radius,
                // and enabling global broadcast for lots of train engines would significantly reduce client FPS.
                PrimaryTrainEngineController.InvokeRandomized(() =>
                {
                    _plugin.TrackStart();

                    if (_genericMarker != null)
                    {
                        _genericMarker.transform.position = PrimaryTrainEngineController.Position;
                        _genericMarker.InvalidateNetworkCache();
                        _genericMarker.SendNetworkUpdate_Position();
                    }

                    if (_vendingMarker != null)
                    {
                        _vendingMarker.transform.position = PrimaryTrainEngineController.Position;
                        _vendingMarker.InvalidateNetworkCache();
                        _vendingMarker.SendNetworkUpdate_Position();
                    }

                    _plugin.TrackEnd();
                }, 0, _config.MapMarkerUpdateIntervalSeconds, _config.MapMarkerUpdateIntervalSeconds * 0.1f);
            }

            private void EnableInvincibility()
            {
                foreach (var trainCarComponent in _trainCarComponents)
                {
                    AutomatedWorkcarts.EnableInvincibility(trainCarComponent.TrainCar);
                }
            }

            private void DisableInvincibility()
            {
                foreach (var trainCarComponent in _trainCarComponents)
                {
                    AutomatedWorkcarts.DisableInvincibility(trainCarComponent.TrainCar);
                }
            }

            private void SetupCollisionTriggers()
            {
                var completeTrain = PrimaryTrainEngine.completeTrain;
                var frontTrigger = completeTrain.frontCollisionTrigger;
                var rearTrigger = completeTrain.rearCollisionTrigger;

                _collisionTriggerA = TrainCollisionTrigger.AddToTrigger(_plugin, frontTrigger, frontTrigger.owner, this);
                _collisionTriggerB = TrainCollisionTrigger.AddToTrigger(_plugin, rearTrigger, rearTrigger.owner, this);
            }

            private void DestroyCinematically()
            {
                foreach (var trainCarComponent in _trainCarComponents.ToArray())
                {
                    DestroyTrainCarCinematically(trainCarComponent.TrainCar);
                }
            }
        }

        private class TrainCollisionTrigger : TriggerBase
        {
            public static TrainCollisionTrigger AddToTrigger(AutomatedWorkcarts plugin, TriggerBase hostTrigger, TrainCar trainCar, TrainController trainController)
            {
                var component = hostTrigger.gameObject.AddComponent<TrainCollisionTrigger>();
                component._plugin = plugin;
                component.interestLayers = hostTrigger.interestLayers;
                component.TrainController = trainController;
                component.TrainCar = trainCar;
                return component;
            }

            public TrainController TrainController { get; private set; }
            public TrainCar TrainCar { get; private set; }
            private AutomatedWorkcarts _plugin;
            private Configuration _config => _plugin._config;

            public override void OnEntityEnter(BaseEntity entity)
            {
                _plugin.TrackStart();
                HandleEntityCollision(entity);
                _plugin.TrackEnd();
            }

            private void HandleEntityCollision(BaseEntity entity)
            {
                var trainCar = entity as TrainCar;
                if ((object)trainCar != null)
                {
                    HandleTrainCar(trainCar);
                }

                if (entity is JunkPile or LootContainer)
                {
                    var entity2 = entity;
                    entity.Invoke(() =>
                    {
                        if (entity2.IsDestroyed)
                            return;

                        entity2.Kill();
                        LogWarning($"Automated train destroyed entity '{entity2.ShortPrefabName}' in its path at {transform.position}.");
                    }, 0);
                }
            }

            private void HandleTrainCar(TrainCar otherTrainCar)
            {
                entityContents ??= new HashSet<BaseEntity>();

                // Ignore if already colliding with that train car.
                if (!entityContents.Add(otherTrainCar))
                    return;

                var otherController = TrainController.TrainManager.GetTrainController(otherTrainCar);

                var forward = TrainController.Forward;
                var otherForward = otherController?.Forward ?? GetTrainCarForward(otherTrainCar);

                if (Vector3.Dot(forward, otherForward) >= 0.01f)
                {
                    // Going same direction.
                    DetermineTrainCarOrientations(TrainCar, forward, otherTrainCar, out var forwardTrainCar);

                    var forwardController = TrainController;
                    var backwardController = otherController;

                    if (forwardTrainCar == otherTrainCar)
                    {
                        forwardController = otherController;
                        backwardController = TrainController;
                    }

                    if (forwardController != null)
                    {
                        forwardController.DepartEarlyIfStoppedOrStopping();
                    }
                    else if (_config.BulldozeOffendingWorkcarts)
                    {
                        LogWarning($"Destroying non-automated train due to blocking an automated train.");
                        ScheduleDestroyTrainCarCinematically(forwardTrainCar);
                        return;
                    }

                    backwardController?.StartChilling(forwardController);
                }
                else
                {
                    // Going opposite directions or perpendicular.
                    if (otherController == null)
                    {
                        if (_config.BulldozeOffendingWorkcarts)
                        {
                            LogWarning($"Destroying non-automated train due to head-on collision with an automated train.");
                            ScheduleDestroyTrainCarCinematically(otherTrainCar);
                        }
                        else
                        {
                            TrainController.StartChilling();
                        }

                        return;
                    }

                    // Don't destroy both, since the collision event can happen for both trains in the same frame.
                    if (TrainController.IsDestroying)
                        return;

                    LogWarning($"Destroying automated train due to head-on collision with another.");
                    if (TrainCar.GetTrackSpeed() < otherTrainCar.GetTrackSpeed())
                    {
                        TrainController.ScheduleCinematicDestruction();
                    }
                    else
                    {
                        otherController.ScheduleCinematicDestruction();
                    }
                }
            }
        }

        private interface ITrainCarComponent
        {
            TrainController TrainController { get; }
            TrainCar TrainCar { get; }
        }

        private class TrainCarComponent : FacepunchBehaviour, ITrainCarComponent
        {
            public static TrainCarComponent AddToEntity(TrainCar trainCar, TrainController trainController)
            {
                var component = trainCar.gameObject.AddComponent<TrainCarComponent>();
                component.TrainController = trainController;
                component.TrainCar = trainCar;
                return component;
            }

            public TrainController TrainController { get; private set; }
            public TrainCar TrainCar { get; private set; }

            private void OnDestroy()
            {
                TrainController.HandleTrainCarDestroyed(this);
            }
        }

        private class TrainEngineController : FacepunchBehaviour, ITrainCarComponent
        {
            public static TrainEngineController AddToEntity(AutomatedWorkcarts plugin, TrainEngine trainEngine, TrainController trainController, bool isReverse = false)
            {
                var trainEngineController = trainEngine.gameObject.AddComponent<TrainEngineController>();
                trainEngineController.Init(plugin, trainEngine, trainController, isReverse);
                return trainEngineController;
            }

            public TrainController TrainController { get; private set; }
            public TrainEngine TrainEngine { get; private set; }
            public Transform Transform { get; private set; }
            public NPCShopKeeper Conductor { get; private set; }
            public ulong NetId { get; private set; }
            public string NetIdString { get; private set; }
            private AutomatedWorkcarts _plugin;
            private bool _isReverse;

            public TrainCar TrainCar => TrainEngine;
            public Vector3 Position => Transform.position;
            private Configuration _config => _plugin._config;

            public void Init(AutomatedWorkcarts plugin, TrainEngine trainEngine, TrainController trainController, bool isReverse)
            {
                _plugin = plugin;
                TrainController = trainController;
                TrainEngine = trainEngine;
                Transform = trainEngine.transform;
                NetId = trainEngine.net.ID.Value;
                NetIdString = NetId.ToString();

                _isReverse = isReverse;

                trainEngine.SetHealth(trainEngine.MaxHealth());

                AddConductor();
                EnableUnlimitedFuel();

                TrainEngine.engineController.TryStartEngine(Conductor);

                // Delay disabling hazard checks since starting the engine is not immediate.
                Invoke(DisableHazardChecks, 1f);

                ExposedHooks.OnWorkcartAutomationStarted(trainEngine);
            }

            public void SetThrottle(EngineSpeeds throttle)
            {
                if (_isReverse && throttle != EngineSpeeds.Zero)
                {
                    throttle = ApplyDirection(throttle, DirectionInstruction.Invert);
                }

                TrainEngine.SetThrottle(throttle);
            }

            public void SetTrackSelection(TrackSelection trackSelection)
            {
                if (_isReverse)
                {
                    trackSelection = ApplyTrackSelection(trackSelection, TrackSelectionInstruction.Swap);
                }

                TrainEngine.SetTrackSelection(trackSelection);
            }

            private BaseMountable GetDriverSeat()
            {
                foreach (var mountPoint in TrainEngine.mountPoints)
                {
                    if (mountPoint.isDriver)
                        return mountPoint.mountable;
                }

                return null;
            }

            private void AddOutfit()
            {
                Conductor.inventory.Strip();

                foreach (var itemInfo in _config.ConductorOutfit)
                {
                    var itemDefinition = itemInfo.GetItemDefinition();
                    if (itemDefinition != null)
                    {
                        Conductor.inventory.containerWear.AddItem(itemDefinition, 1, itemInfo.SkinId);
                    }
                }

                Conductor.SendNetworkUpdate();
            }

            private void AddConductor()
            {
                TrainEngine.DismountAllPlayers();

                var driverSeat = GetDriverSeat();
                if (driverSeat == null)
                    return;

                Conductor = GameManager.server.CreateEntity(ShopkeeperPrefab, driverSeat.transform.position) as NPCShopKeeper;
                if (Conductor == null)
                    return;

                Conductor.EnableSaving(false);
                Conductor.Spawn();

                Conductor.CancelInvoke(Conductor.Greeting);
                Conductor.CancelInvoke(Conductor.TickMovement);

                // Simple and performant way to prevent NPCs and turrets from targeting the conductor.
                Conductor.DisablePlayerCollider();
                Query.Server.RemovePlayer(Conductor);
                Conductor.transform.localScale = Vector3.zero;

                AddOutfit();
                driverSeat.AttemptMount(Conductor, doMountChecks: false);
            }

            private void DisableHazardChecks()
            {
                TrainEngine.SetFlag(Flag_HazardAhead, false);
                TrainEngine.CancelInvoke(TrainEngine.CheckForHazards);
            }

            private void EnableHazardChecks()
            {
                if (TrainEngine.IsOn() && !TrainEngine.IsInvoking(TrainEngine.CheckForHazards))
                {
                    TrainEngine.InvokeRandomized(TrainEngine.CheckForHazards, 0f, 1f, 0.1f);
                }
            }

            private void EnableUnlimitedFuel()
            {
                var fuelSystem = TrainEngine.GetFuelSystem();
                fuelSystem.cachedHasFuel = true;
                fuelSystem.nextFuelCheckTime = float.MaxValue;
            }

            private void DisableUnlimitedFuel()
            {
                TrainEngine.GetFuelSystem().nextFuelCheckTime = 0;
            }

            private void OnDestroy()
            {
                TrainController.HandleTrainCarDestroyed(this);

                if (Conductor != null && !Conductor.IsDestroyed)
                {
                    Conductor.EnsureDismounted();
                    Conductor.Kill();
                }

                if (TrainEngine != null && !TrainEngine.IsDestroyed)
                {
                    DisableUnlimitedFuel();
                    EnableHazardChecks();
                    EnableTrainCoupling(TrainEngine.completeTrain);
                    ExposedHooks.OnWorkcartAutomationStopped(TrainEngine);
                }
            }
        }

        #endregion

        #region Data

        [JsonObject(MemberSerialization.OptIn)]
        private class TrainEngineData
        {
            [JsonProperty("Route", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Route;

            [JsonProperty("Throttle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(StringEnumConverter))]
            public EngineSpeeds? Throttle { get; private set; }

            [JsonProperty("TrackSelection", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(StringEnumConverter))]
            public TrackSelection? TrackSelection { get; private set; }

            public bool UpdateData(EngineSpeeds throttle, TrackSelection trackSelection)
            {
                var changed = false;

                if (Throttle != throttle)
                {
                    Throttle = throttle;
                    changed = true;
                }

                if (TrackSelection != trackSelection)
                {
                    TrackSelection = trackSelection;
                    changed = true;
                }

                return changed;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class StoredPluginData
        {
            public static StoredPluginData Clear()
            {
                var data = new StoredPluginData();
                data.Save();
                return data;
            }

            [JsonProperty("AutomatedWorkcardIds", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public HashSet<ulong> AutomatedWorkcartIds;

            [JsonProperty("AutomatedWorkcarts")]
            public Dictionary<ulong, TrainEngineData> AutomatedTrainEngines = new();

            [JsonIgnore]
            private bool _isDirty;

            public static string Filename => nameof(AutomatedTrainEngines);

            public static StoredPluginData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredPluginData>(Filename) ?? new StoredPluginData();

                // Migrate from the legacy `AutomatedWorkcartIds` to `AutomatedWorkcarts` which supports data.
                if (data.AutomatedWorkcartIds != null)
                {
                    foreach (var trainEngineId in data.AutomatedWorkcartIds)
                    {
                        data.AutomatedTrainEngines[trainEngineId] = new TrainEngineData();
                    }

                    data.AutomatedWorkcartIds = null;
                }

                return data;
            }

            public void Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(Filename, this);
            }

            public void SaveIfDirty()
            {
                if (_isDirty)
                {
                    Save();
                }
            }

            public TrainEngineData GetTrainEngineData(ulong trainCarId)
            {
                return AutomatedTrainEngines.TryGetValue(trainCarId, out var trainEngineData)
                    ? trainEngineData
                    : null;
            }

            public void AddTrainEngineId(ulong trainEngineId, TrainEngineData trainEngineData)
            {
                if (AutomatedTrainEngines.ContainsKey(trainEngineId))
                    return;

                AutomatedTrainEngines[trainEngineId] = trainEngineData;
                _isDirty = true;
            }

            public void RemoveTrainEngineId(ulong trainEngineId)
            {
                if (AutomatedTrainEngines.Remove(trainEngineId))
                {
                    _isDirty = true;
                }
            }

            public void TrimToTrainEngineIds(HashSet<ulong> foundTrainEngineIds)
            {
                foreach (var trainEngineId in AutomatedTrainEngines.Keys.ToArray())
                {
                    if (!foundTrainEngineIds.Contains(trainEngineId))
                    {
                        RemoveTrainEngineId(trainEngineId);
                    }
                }

                SaveIfDirty();
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class StoredMapData
        {
            [JsonProperty("MapTriggers")]
            public List<TriggerData> MapTriggers = new();

            // Return example: proceduralmap.1500.548423.212
            private static string GetPerWipeSaveName()
            {
                return World.SaveFileName.Substring(0, World.SaveFileName.LastIndexOf("."));
            }

            // Return example: proceduralmap.1500.548423
            private static string GetCrossWipeSaveName()
            {
                var saveName = GetPerWipeSaveName();
                return saveName[..saveName.LastIndexOf(".")];
            }

            private static bool IsProcedural() => World.SaveFileName.StartsWith("proceduralmap");

            private static string GetPerWipeFilePath() => $"{nameof(AutomatedWorkcarts)}/{GetPerWipeSaveName()}";
            private static string GetCrossWipeFilePath() => $"{nameof(AutomatedWorkcarts)}/{GetCrossWipeSaveName()}";
            private static string GetFilepath() => IsProcedural() ? GetPerWipeFilePath() : GetCrossWipeFilePath();

            public static StoredMapData Load()
            {
                var filepath = GetFilepath();

                if (Interface.Oxide.DataFileSystem.ExistsDatafile(filepath))
                    return Interface.Oxide.DataFileSystem.ReadObject<StoredMapData>(filepath) ?? new StoredMapData();

                if (!IsProcedural())
                {
                    var perWipeFilepath = GetPerWipeFilePath();
                    if (Interface.Oxide.DataFileSystem.ExistsDatafile(perWipeFilepath))
                    {
                        var data = Interface.Oxide.DataFileSystem.ReadObject<StoredMapData>(perWipeFilepath);
                        if (data != null)
                        {
                            LogWarning($"Migrating map data file from '{perWipeFilepath}.json' to '{filepath}.json'");
                            data.Save();
                            return data;
                        }
                    }
                }

                return new StoredMapData();
            }

            public StoredMapData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(GetFilepath(), this);
                return this;
            }

            public void AddTrigger(TriggerData customTrigger)
            {
                MapTriggers.Add(customTrigger);
                Save();
            }

            public void RemoveTrigger(TriggerData triggerData)
            {
                MapTriggers.Remove(triggerData);
                Save();
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class StoredTunnelData
        {
            private const float DefaultStationStopDuration = 15;
            private const float DefaultQuickStopDuration = 5;
            private const float DefaultTriggerHeight = 0.29f;

            public static string Filename => $"{nameof(AutomatedWorkcarts)}/TunnelTriggers";

            public static StoredTunnelData Load()
            {
                var dataExists = Interface.Oxide.DataFileSystem.ExistsDatafile(Filename);
                var data = dataExists
                    ? Interface.Oxide.DataFileSystem.ReadObject<StoredTunnelData>(Filename) ?? GetDefaultData()
                    : GetDefaultData();

                if (MigrateToLatest(data) && dataExists)
                {
                    data.Save();
                }

                return data;
            }

            private static bool MigrateToLatest(StoredTunnelData data)
            {
                return MigrateTriggersToMaintenanceTunnels(data)
                     | MigrateV0ToV1(data);
            }

            private static bool MigrateTriggersToMaintenanceTunnels(StoredTunnelData data)
            {
                var changed = false;

                foreach (var triggerData in data.TunnelTriggers)
                {
                    var tunnelType = triggerData.GetTunnelType();
                    if (tunnelType == TunnelType.TrainStation)
                    {
                        if (triggerData.Position == new Vector3(0, DefaultTriggerHeight, -84))
                        {
                            triggerData.Position = new Vector3(45, DefaultTriggerHeight, 18);
                            changed = true;
                            continue;
                        }

                        if (triggerData.Position == new Vector3(0, DefaultTriggerHeight, 84))
                        {
                            triggerData.Position = new Vector3(-45, DefaultTriggerHeight, -18);
                            changed = true;
                            continue;
                        }
                    }
                }

                return changed;
            }

            private static bool MigrateV0ToV1(StoredTunnelData data)
            {
                if (data.DataFileVersion != 0)
                    return false;

                data.DataFileVersion++;

                // Add VerticalIntersection triggers.
                data.TunnelTriggers.Add(new TriggerData
                {
                    Id = GetNextTriggerId(data.TunnelTriggers),
                    Position = new Vector3(-85, DefaultTriggerHeight, 3.0f),
                    TunnelType = TunnelType.VerticalIntersection.ToString(),
                    TrackSelection = TrackSelectionInstruction.Default.ToString(),
                });

                data.TunnelTriggers.Add(new TriggerData
                {
                    Id = GetNextTriggerId(data.TunnelTriggers),
                    Position = new Vector3(0, DefaultTriggerHeight, 3.0f),
                    TunnelType = TunnelType.VerticalIntersection.ToString(),
                    TrackSelection = TrackSelectionInstruction.Left.ToString(),
                });

                return true;
            }

            [JsonProperty("DataFileVersion", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float DataFileVersion;

            [JsonProperty("TunnelTriggers")]
            public List<TriggerData> TunnelTriggers = new();

            public StoredTunnelData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(Filename, this);
                return this;
            }

            public void AddTrigger(TriggerData triggerData)
            {
                TunnelTriggers.Add(triggerData);
                Save();
            }

            public void RemoveTrigger(TriggerData triggerData)
            {
                TunnelTriggers.Remove(triggerData);
                Save();
            }

            public static StoredTunnelData GetDefaultData()
            {
                return new StoredTunnelData
                {
                    TunnelTriggers =
                    {
                        new TriggerData
                        {
                            Id = 1,
                            Position = new Vector3(4.5f, DefaultTriggerHeight, 52),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultStationStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 2,
                            Position = new Vector3(45, DefaultTriggerHeight, 18),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            AddConductor = true,
                            Direction = DirectionInstruction.Fwd.ToString(),
                            Speed = SpeedInstruction.Hi.ToString(),
                            TrackSelection = TrackSelectionInstruction.Left.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 3,
                            Position = new Vector3(-4.5f, DefaultTriggerHeight, -11),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultStationStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 4,
                            Position = new Vector3(-45, DefaultTriggerHeight, -18),
                            TunnelType = TunnelType.TrainStation.ToString(),
                            AddConductor = true,
                            Direction = DirectionInstruction.Fwd.ToString(),
                            Speed = SpeedInstruction.Hi.ToString(),
                            TrackSelection = TrackSelectionInstruction.Left.ToString(),
                        },

                        new TriggerData
                        {
                            Id = 5,
                            Position = new Vector3(-4.45f, DefaultTriggerHeight, -31),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Med.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 6,
                            Position = new Vector3(-4.5f, DefaultTriggerHeight, -1f),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = 5,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 7,
                            Position = new Vector3(4.45f, DefaultTriggerHeight, 39),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Med.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 8,
                            Position = new Vector3(4.5f, DefaultTriggerHeight, 9f),
                            TunnelType = TunnelType.BarricadeTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = 5,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },

                        new TriggerData
                        {
                            Id = 9,
                            Position = new Vector3(3, DefaultTriggerHeight, 35f),
                            TunnelType = TunnelType.LootTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultQuickStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                        new TriggerData
                        {
                            Id = 10,
                            Position = new Vector3(-3, DefaultTriggerHeight, -35f),
                            TunnelType = TunnelType.LootTunnel.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultQuickStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },

                        new TriggerData
                        {
                            Id = 11,
                            Position = new Vector3(35, DefaultTriggerHeight, -3.0f),
                            TunnelType = TunnelType.Intersection.ToString(),
                            Brake = true,
                            Speed = SpeedInstruction.Zero.ToString(),
                            StopDuration = DefaultQuickStopDuration,
                            DepartureSpeed = SpeedInstruction.Hi.ToString(),
                        },
                    }
                };
            }
        }

        #endregion

        #region Configuration

        private class ItemInfo
        {
            [JsonProperty("ShortName")]
            public string ShortName;

            [JsonProperty("Skin")]
            public ulong SkinId;

            private bool _isValidated;
            private ItemDefinition _itemDefinition;
            public ItemDefinition GetItemDefinition()
            {
                if (!_isValidated)
                {
                    var itemDefinition = ItemManager.FindItemDefinition(ShortName);
                    if (itemDefinition != null)
                    {
                        _itemDefinition = itemDefinition;
                    }
                    else
                    {
                        LogError($"Invalid item short name in config: '{ShortName}'");
                    }

                    _isValidated = true;
                }

                return _itemDefinition;
            }
        }

        private class GenericMarkerOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Color")]
            public string Color = "#00ff00";

            [JsonProperty("Alpha")]
            public float Alpha = 1;

            [JsonProperty("Radius")]
            public float Radius = 0.05f;

            private Color? _color;
            public Color GetColor()
            {
                _color ??= ParseColor(Color, UnityEngine.Color.black);
                return (Color)_color;
            }

            private static Color ParseColor(string colorString, Color defaultColor)
            {
                return ColorUtility.TryParseHtmlString(colorString, out var color)
                    ? color
                    : defaultColor;
            }
        }

        private class VendingMarkerOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Name")]
            public string Name = "Automated Train";
        }

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("PlayHornForNearbyPlayersInRadius")]
            public float PlayHornForNearbyPlayersInRadius = 0f;

            [JsonProperty("DefaultSpeed")]
            public string DefaultSpeed = EngineSpeeds.Fwd_Hi.ToString();

            [JsonProperty("DefaultTrackSelection")]
            public string DefaultTrackSelection = TrackSelection.Left.ToString();

            [JsonProperty("BulldozeOffendingWorkcarts")]
            public bool BulldozeOffendingWorkcarts = false;

            [JsonProperty("EnableMapTriggers")]
            public bool EnableMapTriggers = true;

            [JsonProperty("EnableTunnelTriggers")]
            public Dictionary<string, bool> EnableTunnelTriggers = new()
            {
                [TunnelType.TrainStation.ToString()] = false,
                [TunnelType.BarricadeTunnel.ToString()] = false,
                [TunnelType.LootTunnel.ToString()] = false,
                [TunnelType.Intersection.ToString()] = false,
                [TunnelType.LargeIntersection.ToString()] = false,
                [TunnelType.VerticalIntersection.ToString()] = false,
            };

            [JsonProperty("MaxConductors")]
            public int MaxConductors = -1;

            [JsonProperty("SpawnTriggersRespectConductorLimit")]
            public bool SpawnTriggersRespectConductorLimit = false;

            [JsonProperty("ConductorOutfit")]
            public ItemInfo[] ConductorOutfit =
            {
                new() { ShortName = "jumpsuit.suit" },
                new() { ShortName = "sunglasses03chrome" },
                new() { ShortName = "hat.boonie" },
            };

            [JsonProperty("ColoredMapMarker")]
            public GenericMarkerOptions GenericMapMarker = new();

            [JsonProperty("VendingMapMarker")]
            public VendingMarkerOptions VendingMapMarker = new();

            [JsonProperty("MapMarkerUpdateInveralSeconds")]
            private float DeprecatedMapMarkerUpdateIntervalSeconds { set => MapMarkerUpdateIntervalSeconds = value; }

            [JsonProperty("MapMarkerUpdateIntervalSeconds")]
            public float MapMarkerUpdateIntervalSeconds = 5.0f;

            [JsonProperty("TriggerDisplayDistance")]
            public float TriggerDisplayDistance = 150;

            public bool IsTunnelTypeEnabled(TunnelType tunnelType)
            {
                return EnableTunnelTriggers.TryGetValue(tunnelType.ToString(), out var enabled) && enabled;
            }

            private EngineSpeeds? _defaultSpeed;
            public EngineSpeeds GetDefaultSpeed()
            {
                if (_defaultSpeed != null)
                    return (EngineSpeeds)_defaultSpeed;

                if (TryParseEngineSpeed(DefaultSpeed, out var engineSpeed))
                {
                    _defaultSpeed = engineSpeed;
                    return engineSpeed;
                }

                return EngineSpeeds.Fwd_Hi;
            }

            private TrackSelection? _defaultTrackSelection;
            public TrackSelection GetDefaultTrackSelection()
            {
                if (_defaultTrackSelection != null)
                    return (TrackSelection)_defaultTrackSelection;

                if (TryParseTrackSelection(DefaultTrackSelection, out var trackSelection))
                {
                    _defaultTrackSelection = trackSelection;
                    return trackSelection;
                }

                return TrackSelection.Left;
            }
        }

        private Configuration GetDefaultConfig() => new();

        #endregion

        #region Configuration Helpers

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            var changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var currentDictValue = currentRawValue as Dictionary<string, object>;
                    if (currentWithDefaults[key] is Dictionary<string, object> defaultDictValue)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
            GetMessage(player.UserIDString, messageName, args);

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string GetTriggerOptions(IPlayer player)
        {
            var speedOptions = GetMessage(player, Lang.HelpSpeedOptions, GetEnumOptions<SpeedInstruction>());
            var directionOptions = GetMessage(player, Lang.HelpDirectionOptions, GetEnumOptions<DirectionInstruction>());
            var trackSelectionOptions = GetMessage(player, Lang.HelpTrackSelectionOptions, GetEnumOptions<TrackSelectionInstruction>());
            var trainCarOptions = GetMessage(player, Lang.HelpTrainCarOptions, FormatOptions(TrainCarPrefab.GetAliases()));
            var otherOptions = GetMessage(player, Lang.HelpOtherOptions);

            return $"{speedOptions}\n{directionOptions}\n{trackSelectionOptions}\n{trainCarOptions}\n{otherOptions}";
        }

        private string GetTriggerPrefix(IPlayer player, TrainTriggerType triggerType) =>
            GetMessage(player, triggerType == TrainTriggerType.Tunnel ? Lang.InfoTriggerTunnelPrefix : Lang.InfoTriggerMapPrefix);

        private string GetTriggerPrefix(IPlayer player, TriggerData triggerData) =>
            GetTriggerPrefix(player, triggerData.TriggerType);

        private string GetTriggerPrefix(BasePlayer player, TrainTriggerType triggerType) =>
            GetTriggerPrefix(player.IPlayer, triggerType);

        private string GetTriggerPrefix(BasePlayer player, TriggerData triggerData) =>
            GetTriggerPrefix(player.IPlayer, triggerData.TriggerType);

        private string GetConductorCountMessage(IPlayer player)
        {
            return _config.MaxConductors >= 0
                ? GetMessage(player, Lang.InfoConductorCountLimited, _trainManager.CountedConductors,
                    _config.MaxConductors)
                : GetMessage(player, Lang.InfoConductorCountUnlimited, _trainManager.CountedConductors);
        }

        private static class Lang
        {
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorNoTriggers = "Error.NoTriggers";
            public const string ErrorTriggerNotFound = "Error.TriggerNotFound";
            public const string ErrorNoTrackFound = "Error.ErrorNoTrackFound";
            public const string ErrorNoWorkcartFound = "Error.NoWorkcartFound";
            public const string ErrorNoWorkcart = "Error.NoWorkcart";
            public const string ErrorAutomateBlocked = "Error.AutomateBlocked";
            public const string ErrorUnsupportedTunnel = "Error.UnsupportedTunnel";
            public const string ErrorTunnelTypeDisabled = "Error.TunnelTypeDisabled";
            public const string ErrorMapTriggersDisabled = "Error.MapTriggersDisabled";
            public const string ErrorMaxConductors = "Error.MaxConductors";
            public const string ErrorWorkcartOwned = "Error.WorkcartOwned";
            public const string ErrorNoAutomatedWorkcarts = "Error.NoAutomatedWorkcarts";
            public const string ErrorRequiresSpawnTrigger = "Error.RequiresSpawnTrigger";
            public const string ErrorTriggerDisabled = "Error.TriggerDisabled";
            public const string ErrorUnrecognizedTrainCar = "Error.UnrecognizedTrainCar";

            public const string ToggleOnSuccess = "Toggle.Success.On";
            public const string ToggleOnWithRouteSuccess = "Toggle.Success.On.WithRoute";
            public const string ToggleOffSuccess = "Toggle.Success.Off";
            public const string ResetAllSuccess = "ResetAll.Success";
            public const string ShowTriggersSuccess = "ShowTriggers.Success";
            public const string ShowTriggersWithRouteSuccess = "ShowTriggers.WithRoute.Success";

            public const string AddTriggerSyntax = "AddTrigger.Syntax";
            public const string AddTriggerSuccess = "AddTrigger.Success";
            public const string MoveTriggerSuccess = "MoveTrigger.Success";
            public const string RotateTriggerSuccess = "RotateTrigger.Success";
            public const string UpdateTriggerSyntax = "UpdateTrigger.Syntax";
            public const string UpdateTriggerSuccess = "UpdateTrigger.Success";
            public const string SimpleTriggerSyntax = "Trigger.SimpleSyntax";
            public const string RemoveTriggerSuccess = "RemoveTrigger.Success";

            public const string AddCommandSyntax = "AddCommand.Syntax";
            public const string RemoveCommandSyntax = "RemoveCommand.Syntax";
            public const string RemoveCommandErrorIndex = "RemoveCommand.Error.Index";

            public const string InfoConductorCountLimited = "Info.ConductorCount.Limited";
            public const string InfoConductorCountUnlimited = "Info.ConductorCount.Unlimited";

            public const string HelpSpeedOptions = "Help.SpeedOptions";
            public const string HelpDirectionOptions = "Help.DirectionOptions";
            public const string HelpTrackSelectionOptions = "Help.TrackSelectionOptions";
            public const string HelpTrainCarOptions = "Help.HelpTrainCarOptions";
            public const string HelpOtherOptions = "Help.OtherOptions3";

            public const string InfoTrigger = "Info.Trigger";
            public const string InfoTriggerMapPrefix = "Info.Trigger.Prefix.Map";
            public const string InfoTriggerTunnelPrefix = "Info.Trigger.Prefix.Tunnel";

            public const string InfoTriggerDisabled = "Info.Trigger.Disabled";
            public const string InfoTriggerMap = "Info.Trigger.Map";
            public const string InfoTriggerRoute = "Info.Trigger.Route";
            public const string InfoTriggerTunnel = "Info.Trigger.Tunnel";
            public const string InfoTriggerSpawner = "Info.Trigger.Spawner2";
            public const string InfoTriggerAddConductor = "Info.Trigger.Conductor";
            public const string InfoTriggerDestroy = "Info.Trigger.Destroy";
            public const string InfoTriggerStopDuration = "Info.Trigger.StopDuration";

            public const string InfoTriggerSpeed = "Info.Trigger.Speed";
            public const string InfoTriggerBrakeToSpeed = "Info.Trigger.BrakeToSpeed";
            public const string InfoTriggerDepartureSpeed = "Info.Trigger.DepartureSpeed";
            public const string InfoTriggerDirection = "Info.Trigger.Direction";
            public const string InfoTriggerDepartureDirection = "Info.Trigger.DepartureDirection";
            public const string InfoTriggerTrackSelection = "Info.Trigger.TrackSelection";
            public const string InfoTriggerCommands = "Info.Trigger.Command";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorNoTriggers] = "There are no workcart triggers on this map.",
                [Lang.ErrorTriggerNotFound] = "Error: Trigger id #<color=#fd4>{0}{1}</color> not found.",
                [Lang.ErrorNoTrackFound] = "Error: No track found nearby.",
                [Lang.ErrorNoWorkcartFound] = "Error: No workcart found.",
                [Lang.ErrorNoWorkcart] = "Error: That train has no workcarts.",
                [Lang.ErrorAutomateBlocked] = "Error: Another plugin blocked automating that workcart.",
                [Lang.ErrorUnsupportedTunnel] = "Error: Not a supported train tunnel.",
                [Lang.ErrorTunnelTypeDisabled] = "Error: Tunnel type <color=#fd4>{0}</color> is currently disabled.",
                [Lang.ErrorMapTriggersDisabled] = "Error: Map triggers are disabled.",
                [Lang.ErrorMaxConductors] = "Error: There are already <color=#fd4>{0}</color> out of <color=#fd4>{1}</color> conductors.",
                [Lang.ErrorWorkcartOwned] = "Error: That workcart has an owner.",
                [Lang.ErrorNoAutomatedWorkcarts] = "Error: There are no automated workcarts.",
                [Lang.ErrorRequiresSpawnTrigger] = "Error: That is not a spawn trigger.",
                [Lang.ErrorTriggerDisabled] = "Error: That trigger is disabled.",
                [Lang.ErrorUnrecognizedTrainCar] = "Error: Unrecognized train car: {0}.",

                [Lang.ToggleOnSuccess] = "That workcart is now automated.",
                [Lang.ToggleOnWithRouteSuccess] = "That workcart is now automated with route <color=#fd4>@{0}</color>.",
                [Lang.ToggleOffSuccess] = "That workcart is no longer automated.",
                [Lang.ResetAllSuccess] = "All {0} conductors have been removed.",
                [Lang.ShowTriggersSuccess] = "Showing all triggers for <color=#fd4>{0}</color>.",
                [Lang.ShowTriggersWithRouteSuccess] = "Showing all triggers for route <color=#fd4>@{0}</color> for <color=#fd4>{1}</color>",

                [Lang.AddTriggerSyntax] = "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
                [Lang.AddTriggerSuccess] = "Successfully added trigger #<color=#fd4>{0}{1}</color>.",
                [Lang.UpdateTriggerSyntax] = "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
                [Lang.UpdateTriggerSuccess] = "Successfully updated trigger #<color=#fd4>{0}{1}</color>",
                [Lang.MoveTriggerSuccess] = "Successfully moved trigger #<color=#fd4>{0}{1}</color>",
                [Lang.RotateTriggerSuccess] = "Successfully rotated trigger #<color=#fd4>{0}{1}</color>",
                [Lang.SimpleTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #<color=#fd4>{0}{1}</color> successfully removed.",

                [Lang.AddCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <command></color>",
                [Lang.RemoveCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <number></color>",
                [Lang.RemoveCommandErrorIndex] = "Error: Invalid command index <color=#fd4>{0}</color>.",

                [Lang.InfoConductorCountLimited] = "Total conductors: <color=#fd4>{0}/{1}</color>.",
                [Lang.InfoConductorCountUnlimited] = "Total conductors: <color=#fd4>{0}</color>.",

                [Lang.HelpSpeedOptions] = "Speeds: {0}",
                [Lang.HelpDirectionOptions] = "Directions: {0}",
                [Lang.HelpTrackSelectionOptions] = "Track selection: {0}",
                [Lang.HelpTrainCarOptions] = "Train car options: {0}",
                [Lang.HelpOtherOptions] = "Other options: <color=#fd4>Conductor</color> | <color=#fd4>Brake</color> | <color=#fd4>Destroy</color> | <color=#fd4>@ROUTE_NAME</color> | <color=#fd4>Enabled</color> | <color=#fd4>Disabled</color>",

                [Lang.InfoTrigger] = "Workcart Trigger #{0}{1}",
                [Lang.InfoTriggerMapPrefix] = "M",
                [Lang.InfoTriggerTunnelPrefix] = "T",

                [Lang.InfoTriggerDisabled] = "DISABLED",
                [Lang.InfoTriggerMap] = "Map-specific",
                [Lang.InfoTriggerRoute] = "Route: @{0}",
                [Lang.InfoTriggerTunnel] = "Tunnel type: {0} (x{1})",
                [Lang.InfoTriggerSpawner] = "Spawns: {0}",
                [Lang.InfoTriggerAddConductor] = "Adds Conductor",
                [Lang.InfoTriggerDestroy] = "Destroys workcart",
                [Lang.InfoTriggerStopDuration] = "Stop duration: {0}s",

                [Lang.InfoTriggerSpeed] = "Speed: {0}",
                [Lang.InfoTriggerBrakeToSpeed] = "Brake to speed: {0}",
                [Lang.InfoTriggerDepartureSpeed] = "Departure speed: {0}",
                [Lang.InfoTriggerDirection] = "Direction: {0}",
                [Lang.InfoTriggerDepartureDirection] = "Departure direction: {0}",
                [Lang.InfoTriggerTrackSelection] = "Track selection: {0}",
                [Lang.InfoTriggerCommands] = "Commands: {0}",
            }, this, "en");

            // Brazilian Portuguese
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "Você não tem permissão para fazer isso.",
                [Lang.ErrorNoTriggers] = "Não há gatilhos de carrinho de trabalho neste mapa.",
                [Lang.ErrorTriggerNotFound] = "Erro: Trigger id #<color=#fd4>{0}{1}</color> não encontrado.",
                [Lang.ErrorNoTrackFound] = "Erro: nenhuma trilha encontrada nas proximidades.",
                [Lang.ErrorNoWorkcartFound] = "Erro: Nenhum carrinho de trabalho encontrado.",
                [Lang.ErrorNoWorkcart] = "Erro: esse trem não tem carrinhos de trabalho",
                [Lang.ErrorAutomateBlocked] = "Erro: outro plug-in bloqueado automatizando esse carrinho de trabalho.",
                [Lang.ErrorUnsupportedTunnel] = "Erro: não é um túnel ferroviário compatível.",
                [Lang.ErrorTunnelTypeDisabled] = "Erro: o tipo de túnel <color=#fd4>{0}</color> está atualmente desativado.",
                [Lang.ErrorMapTriggersDisabled] = "Erro: os gatilhos do mapa estão desativados.",
                [Lang.ErrorMaxConductors] = "Erro: já existem <color=#fd4>{0}</color> de <color=#fd4>{1}</color>condutores.",
                [Lang.ErrorWorkcartOwned] = "Erro: esse carrinho de trabalho tem um proprietário.",
                [Lang.ErrorNoAutomatedWorkcarts] = "Erro: não há carrinhos de trabalho automatizados.",
                [Lang.ErrorRequiresSpawnTrigger] = "Erro: Isso não é um gatilho de desova.",
                [Lang.ErrorTriggerDisabled] = "Erro: esse gatilho está desativado.",
                [Lang.ErrorUnrecognizedTrainCar] = "Erro: Vagão de trem não reconhecido: {0}.",

                [Lang.ToggleOnSuccess] = "Esse carrinho de trabalho agora é automatizado.",
                [Lang.ToggleOnWithRouteSuccess] = "Esse carrinho de trabalho agora é automatizado com rota <color=#fd4>@{0}</color>.",
                [Lang.ToggleOffSuccess] = "Esse carrinho de trabalho não é mais automatizado.",
                [Lang.ResetAllSuccess] = "Todos os {0} condutores foram removidos.",
                [Lang.ShowTriggersSuccess] = "Mostrando todos os gatilhos para <color=#fd4>{0}</color>.",
                [Lang.ShowTriggersWithRouteSuccess] = "Mostrando todos os gatilhos para a rota <color=#fd4>@{0}</color> para <color=#fd4>{1}</color>",

                [Lang.AddTriggerSyntax] = "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
                [Lang.AddTriggerSuccess] = "Gatilho adicionado com sucesso #<color=#fd4>{0}{1}</color>.",
                [Lang.UpdateTriggerSyntax] = "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
                [Lang.UpdateTriggerSuccess] = "Gatilho atualizado com sucesso #<color=#fd4>{0}{1}</color>",
                [Lang.MoveTriggerSuccess] = "Gatilho movido com sucesso #<color=#fd4>{0}{1}</color>",
                [Lang.RotateTriggerSuccess] = "Gatilho girado com sucesso #<color=#fd4>{0}{1}</color>",
                [Lang.SimpleTriggerSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.RemoveTriggerSuccess] = "Trigger #<color=#fd4>{0}{1}</color> removido com sucesso.",

                [Lang.AddCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <comando></color>",
                [Lang.RemoveCommandSyntax] = "Syntax: <color=#fd4>{0} <id> <número></color>",
                [Lang.RemoveCommandErrorIndex] = "Erro: índice de comando inválido <color=#fd4>{0}</color>.",

                [Lang.InfoConductorCountLimited] = "Condutores totais: <color=#fd4>{0}/{1}</color>.",
                [Lang.InfoConductorCountUnlimited] = "Condutores totais: <color=#fd4>{0}</color>.",

                [Lang.HelpSpeedOptions] = "Velocidades: {0}",
                [Lang.HelpDirectionOptions] = "Direções: {0}",
                [Lang.HelpTrackSelectionOptions] = "Seleção de faixa: {0}",
                [Lang.HelpTrainCarOptions] = "Opções de vagões: {0}",
                [Lang.HelpOtherOptions] = "Outras opções: <color=#fd4>Conductor</color> | <color=#fd4>Brake</color> | <color=#fd4>Destroy</color> | <color=#fd4>@ROUTE_NAME</color> | <color=#fd4>Enabled</color> | <color=#fd4>Disabled</color>",

                [Lang.InfoTrigger] = "Acionador de carrinho de trabalho #{0}{1}",
                [Lang.InfoTriggerMapPrefix] = "M",
                [Lang.InfoTriggerTunnelPrefix] = "T",

                [Lang.InfoTriggerDisabled] = "DESATIVADO",
                [Lang.InfoTriggerMap] = "Específico do mapa",
                [Lang.InfoTriggerRoute] = "Rota: @{0}",
                [Lang.InfoTriggerTunnel] = "Tipo de túnel: {0} (x{1})",
                [Lang.InfoTriggerSpawner] = "Gera {0}",
                [Lang.InfoTriggerAddConductor] = "Adiciona Condutor",
                [Lang.InfoTriggerDestroy] = "Destrói o carrinho de trabalho",
                [Lang.InfoTriggerStopDuration] = "Duração da parada: {0}s",

                [Lang.InfoTriggerSpeed] = "Velocidade: {0}",
                [Lang.InfoTriggerBrakeToSpeed] = "Freie para aumentar a velocidade: {0}",
                [Lang.InfoTriggerDepartureSpeed] = "Velocidade de partida: {0}",
                [Lang.InfoTriggerDirection] = "Direção: {0}",
                [Lang.InfoTriggerDepartureDirection] = "Direção de partida: {0}",
                [Lang.InfoTriggerTrackSelection] = "Seleção de faixa: {0}",
                [Lang.InfoTriggerCommands] = "Eventos: {0}",
            }, this, "pt-BR");
        }

        #endregion
    }
}
