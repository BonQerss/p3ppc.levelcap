using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using p3ppc.levelcap.Template;
using Reloaded.Hooks.ReloadedII.Interfaces;
using p3ppc.levelcap.Configuration;
using static p3ppc.expShare.Native;
using Reloaded.Mod.Interfaces.Structs;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using p3ppc.expShare.NuGet.templates.defaultPlus;
using p3ppc.expShare;
using System.ComponentModel.Design;

namespace p3ppc.levelcap
{/// <summary>
 /// Your mod logic goes here.
 /// </summary>
    public unsafe class Mod : ModBase // <= Do not Remove.
    {
        /// <summary>
        /// Provides access to the mod loader API.
        /// </summary>
        private readonly IModLoader _modLoader;

        /// <summary>
        /// Provides access to the Reloaded.Hooks API.
        /// </summary>
        /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
        private readonly IReloadedHooks? _hooks;

        /// <summary>
        /// Provides access to the Reloaded logger.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Entry point into the mod, instance that created this class.
        /// </summary>
        private readonly IMod _owner;

        /// <summary>
        /// Provides access to this mod's configuration.
        /// </summary>
        private Config _configuration;

        /// <summary>
        /// The configuration of the currently executing mod.
        /// </summary>
        private readonly IModConfig _modConfig;

        private IHook<SetupResultsExpDelegate> _setupExpHook;
        private IHook<GivePartyMemberExpDelegate> _givePartyMemberExpHook;
        private IHook<LevelUpPartyMemberDelegate> _levelUpPartyMemberHook;
        private delegate int GetTotalDayDelegate();
        private GetTotalDayDelegate _getTotalDay;

        private Dictionary<PartyMember, int> _expGains = new();
        private Dictionary<PartyMember, PersonaStatChanges> _levelUps = new();
        private short[] _available = new short[9];
        private int _numAvailable = 0;

        private delegate void GiveProtagExpDelegate(IntPtr results, IntPtr param2, IntPtr param3, IntPtr param4);
        private delegate void GivePersonaExpDelegate(IntPtr persona, uint exp);
        private delegate IntPtr GetProtagPersonaDelegate(short slot);
        private delegate IntPtr GetPartyMemberPersonaDelegate(IntPtr partyMemberInfo);
        private delegate byte GetPersonaLevelDelegate(IntPtr persona);
        private delegate byte GetPartyMemberLevelDelegate(IntPtr partyMemberInfo);
        private delegate ushort GetNumPersonasDelegate();
        private delegate short* GetPersonaSkillsDelegate(IntPtr persona);

        private IHook<GiveProtagExpDelegate> _giveProtagExpHook;
        private IHook<GivePersonaExpDelegate> _givePersonaExpHook;

        private GetProtagPersonaDelegate _getProtagPersona;
        private GetPartyMemberPersonaDelegate _getPartyMemberPersona;
        private GetPersonaLevelDelegate _getPersonaLevel;
        private GetPartyMemberLevelDelegate _getPartyMemberLevel;
        private GetNumPersonasDelegate _getNumPersonas;
        private GetPersonaSkillsDelegate _getPersonaSkills;

        private SortedDictionary<int, int> _levelCaps => new SortedDictionary<int, int>
        {
            { 0x26, _configuration.May9Cap },
            { 0x44, _configuration.June8Cap },
            { 0x61, _configuration.July7Cap },
            { 0x79, _configuration.Aug6Cap },
            { 0x8F, _configuration.Sep5Cap },
            { 0x9D, _configuration.Oct4Cap },
            { 0xE1, _configuration.Nov3Cap },
            { 0xF4, _configuration.Nov22Cap },
            { 0x131, _configuration.January31Cap }
        };

        private bool _isGivingExp = false;

        public Mod(ModContext context)
        {
            _modLoader = context.ModLoader;
            _hooks = context.Hooks;
            _logger = context.Logger;
            _owner = context.Owner;
            _configuration = context.Configuration;
            _modConfig = context.ModConfig;

            Utils.Initialise(_logger, _configuration, _modLoader);
            Native.Initialise(_hooks);

            Utils.SigScan("48 89 54 24 ?? 48 89 4C 24 ?? 53 55 56 57 41 54 41 55 48 83 EC 68", "SetupResultsExp", address =>
            {
                _setupExpHook = _hooks.CreateHook<SetupResultsExpDelegate>(SetupResultsExp, address).Activate();
            });

            Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 83 EC 20 48 89 CF 48 8D 0D ?? ?? ?? ??", "GivePartyMemberExp", address =>
            {
                _givePartyMemberExpHook = _hooks.CreateHook<GivePartyMemberExpDelegate>(GivePartyMemberExp, address).Activate();
            });

            Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 81 EC B0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B E9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B CD", "LevelUpPartyMember", address =>
            {
                _levelUpPartyMemberHook = _hooks.CreateHook<LevelUpPartyMemberDelegate>(LevelUpPartyMember, address).Activate();
            });

            // Get GetTotalDay function
            Utils.SigScan("E8 ?? ?? ?? ?? 0F BF C8 89 0D ?? ?? ?? ?? 89 74 24 ??", "GetTotalDay", address =>
            {
                var funcAddress = Utils.GetGlobalAddress((nint)(address + 1));
                _getTotalDay = _hooks.CreateWrapper<GetTotalDayDelegate>((long)funcAddress, out _);
                _logger.WriteLine($"Found GetTotalDay at 0x{funcAddress:X}");
            });

            Utils.SigScan("40 53 48 83 EC 20 48 89 CB 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 43 ?? 48 83 C4 20", "GetPersonaLevel", address =>
            {
                _getPersonaLevel = _hooks.CreateWrapper<GetPersonaLevelDelegate>(address, out _);
                _logger.WriteLine($"Found GetPersonaLevel at 0x{address:X}");
            });

            Utils.SigScan("E8 ?? ?? ?? ?? 45 33 D2 4C 8B D8", "GetPersonaSkills", address =>
            {
                var funcAddress = Utils.GetGlobalAddress((nint)(address + 1));
                _getPersonaSkills = _hooks.CreateWrapper<GetPersonaSkillsDelegate>((long)funcAddress, out _);
                _logger.WriteLine($"Found GetPersonaSkills at 0x{address:X}");
            });


        }

        private int GetCurrentLevelCap()
        {
            int currentDay = _getTotalDay();
            _logger.WriteLine($"[GetCurrentLevelCap] Current day: 0x{currentDay:X}");

            int previousKey = 0;
            int previousCap = 1;

            foreach (var kvp in _levelCaps)
            {
                int key = kvp.Key;
                int cap = kvp.Value;

                if (currentDay < key)
                {
                    // current day is between previousKey and key
                    // you want the CAP for the *later* key, not the previous one
                    _logger.WriteLine($"[GetCurrentLevelCap] Current day ({currentDay}) is before {key}, returning cap {cap}");
                    return cap;
                }
                previousKey = key;
                previousCap = cap;
            }

            // currentDay >= last key, return last cap
            _logger.WriteLine($"[GetCurrentLevelCap] Current day >= last cap date ({previousKey}), returning last cap {previousCap}");
            return previousCap;
        }


        private bool IsPersonaAtLevelCap(IntPtr persona)
        {
            byte currentLevel = 0;
            currentLevel = _getPersonaLevel(persona);
            _logger.WriteLine($"[IsPersonaAtLevelCap] Persona current level: {currentLevel}");
            int levelCap = GetCurrentLevelCap();
            bool atCap = currentLevel >= levelCap;
            _logger.WriteLine($"[IsPersonaAtLevelCap] Level cap: {levelCap}, At cap: {atCap}");
            return atCap;
        }

        private bool IsPartyMemberAtLevelCap(IntPtr member)
        {
            byte currentLevel = 0;
            currentLevel = _getPartyMemberLevel(member);
            _logger.WriteLine($"[IsPartyMemberAtLevelCap] Party member current level: {currentLevel}");
            int levelCap = GetCurrentLevelCap();
            bool atCap = currentLevel >= levelCap;
            _logger.WriteLine($"[IsPartyMemberAtLevelCap] Level cap: {levelCap}, At cap: {atCap}");
            return atCap;
        }

        private bool IsProtagAtLevelCap()
        {
            var activePersona = GetPartyMemberPersona(PartyMember.Protag);
            if (activePersona == null) return false;

            byte currentLevel = _getPersonaLevel(new IntPtr(activePersona));
            _logger.WriteLine($"[IsProtagAtLevelCap] Protagonist current level: {currentLevel}");
            int levelCap = GetCurrentLevelCap();
            bool atCap = currentLevel >= levelCap;
            _logger.WriteLine($"[IsProtagAtLevelCap] Level cap: {levelCap}, At cap: {atCap}");
            return atCap;
        }

        private int CalculateCappedExp(Persona* persona, int gainedExp, int levelCap)
        {
            var currentLevel = persona->Level;
            var currentExp = persona->Exp;

            if (currentLevel >= levelCap)
            {
                return 0;
            }

            int newExpTotal = currentExp + gainedExp;

            var expRequiredForCap = GetPersonaRequiredExp(persona, (ushort)levelCap);

            if (expRequiredForCap == 0 || expRequiredForCap <= currentExp)
            {
                Utils.LogDebug($"Invalid EXP requirement detected: Required {expRequiredForCap}, Current {currentExp}. Setting gain to 0.");
                return 0;
            }

            if (newExpTotal > expRequiredForCap)
            {
                int cappedGainedExp = expRequiredForCap - currentExp;
                Utils.LogDebug($"EXP overflow detected: Original gain {gainedExp}, capped to {cappedGainedExp} to reach level cap {levelCap}");
                return Math.Max(0, cappedGainedExp);
            }

            return gainedExp;
        }

        private int CalculateCappedExpForProtag(int currentExp, int gainedExp, int levelCap)
        {
            var activePersona = GetPartyMemberPersona(PartyMember.Protag);
            if (activePersona == null) return 0;

            byte currentLevel = _getPersonaLevel(new IntPtr(activePersona));

            if (currentLevel >= levelCap)
            {
                return 0;
            }

            return gainedExp;
        }

        private byte GetPartyMemberLevel(PartyMember member)
        {
            var persona = GetPartyMemberPersona(member);
            if (persona == null)
                return 0;
            return persona->Level;
        }


        private void SetupResultsExp(BattleResults* results, astruct_2* param_2)
        {
            fixed (short* party = &_available[0])
            {
                _numAvailable = GetAvailableParty(party);
            }
            _setupExpHook.OriginalFunction(results, param_2);

            int levelCap = GetCurrentLevelCap();
            if (IsProtagAtLevelCap())
            {
                Utils.LogDebug($"Protagonist is at/above level cap ({levelCap}), zeroing EXP gain.");
                results->GainedExp = 0;
            }
            else
            {
                int cappedExp = CalculateCappedExpForProtag(0, (int)results->GainedExp, levelCap);
                if (cappedExp != (int)results->GainedExp)
                {
                    Utils.LogDebug($"Protagonist EXP capped from {results->GainedExp} to {cappedExp}.");
                    results->GainedExp = cappedExp;
                }
            }

            for (PartyMember member = PartyMember.Yukari; member <= PartyMember.Koromaru; member++)
            {
                _expGains.Remove(member);
                _levelUps.Remove(member);

                bool isInActiveParty = false;
                for (int i = 0; i < 4; i++)
                {
                    if (results->PartyMembers[i] == (short)member)
                    {
                        isInActiveParty = true;
                        break;
                    }
                }

                if (!isInActiveParty) continue;

                var persona = GetPartyMemberPersona(member);
                var level = persona->Level;
                if (level >= 99 || level >= levelCap)
                {
                    Utils.LogDebug($"{member} is above or at level cap ({level} >= {levelCap}), skipping EXP gain.");
                    continue;
                }


                // non functional experimental changes
                // "works" but causes currentexp for party members to be 0, forcing them to never level up again
                // gott
                //{
                //    Utils.LogDebug($"{member} is above or at level cap ({level} >= {levelCap}), skipping EXP gain.");
                //
                //    // Clear LevelUpStatus for this member (bit 4)
                //    results->LevelUpStatus &= unchecked((ushort)~0x10);
//
                //    // Clear LevelIncrease field
                //    for (int i = 0; i < 4; i++)
                //    {
               //         if (results->PartyMembers[i] == (short)member)
               //         {
               //             (&results->PersonaChanges)[i].LevelIncrease = 0;
               //             break;
               //         }
               //     }
//
               //     // Remove from level up tracking dictionary
              //      _levelUps.Remove(member);
              //      continue;
              //  }



                int gainedExp = (int)(CalculateGainedExp(level, param_2));
                int cappedExp = CalculateCappedExp(persona, gainedExp, levelCap);
                if (gainedExp > 0)
                {
                    if (cappedExp != gainedExp)
                    {
                        Utils.LogDebug($"{member} EXP capped from {gainedExp} to {cappedExp}.");
                    }

                    if (cappedExp == 0)
                    {
                        Utils.LogDebug($"{member} is capped — no EXP will be applied.");
                        continue;
                    }

                    gainedExp = cappedExp;
                }

                Utils.LogDebug($"Gained exp is {gainedExp} and Capped EXP is {cappedExp}.");
                var currentExp = persona->Exp;
                var requiredExp = GetPersonaRequiredExp(persona, (ushort)(level + 1));
                Utils.LogDebug($"Required EXP is is {requiredExp}");
                _expGains[member] = gainedExp;

                Utils.LogDebug($"Required EXP is is {requiredExp} something {currentExp} is currentExp and {gainedExp} is gainedEXP");

                if (requiredExp <= currentExp + gainedExp)
                {
                    Utils.LogDebug($"{member} is ready to level up");
                    results->LevelUpStatus |= 0x10;
                    var statChanges = new PersonaStatChanges { };
                    GenerateLevelUpPersona(persona, &statChanges, gainedExp);
                    _levelUps[member] = statChanges;
                }
            }

            var activePersona = GetPartyMemberPersona(PartyMember.Protag);
            if (activePersona == null)
            {
                Utils.LogError("Failed to get active persona for protagonist!");
                return;
            }

            short activePersonaId = activePersona->Id;

            for (short i = 0; i < 12; i++)
            {
                var persona = GetProtagPersona(i);
                if (persona == (Persona*)0)
                    continue;

                var level = persona->Level;
                if (level >= 99 || level >= levelCap)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is above or at level cap ({level} >= {levelCap}), zeroing EXP gain.");
                    results->ProtagExpGains[i] = 0;
                    continue;
                }

                uint originalExp = results->ProtagExpGains[i];
                if (originalExp == 0) continue;

                int cappedExp = CalculateCappedExp(persona, (int)originalExp, levelCap);
                if (cappedExp != (int)originalExp)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) EXP capped from {originalExp} to {cappedExp}.");
                    results->ProtagExpGains[i] = (uint)cappedExp;
                }

                if (cappedExp == 0)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is capped — EXP set to 0.");
                    results->ProtagExpGains[i] = 0;
                    continue;
                }
                var currentExp = persona->Exp;
                var requiredExp = GetPersonaRequiredExp(persona, (ushort)(level + 1));
                if (requiredExp <= currentExp + cappedExp)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is ready to level up with capped EXP");
                }
                else if (requiredExp <= currentExp + (int)originalExp)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) level up prevented by level cap");
                    GenerateLevelUpPersona(persona, &(&results->ProtagPersonaChanges)[i], cappedExp);
                }
            }

                // more broken experimental changes
                // also causes current exp to be 0
                // gotta figure out common thread
                // for (int i = 0; i < 4; i++)
                //{
                //    PartyMember member = (PartyMember)results->PartyMembers[i];
                //    if (member == PartyMember.None) continue;
                //
                //    byte level = GetPartyMemberLevel(member);
                 //   bool overCap = level >= 99 || level >= levelCap;
                 //   bool gotNoExp = results->ExpGains[i] == 0;
//
                //    if (overCap)
                //    {
                //        (&results->PersonaChanges)[i].LevelIncrease = 0;
               ///         results->LevelUpStatus &= unchecked((ushort)~(1 << (i * 4)));
                //        _levelUps.Remove(member);

                 //       Utils.LogDebug($"{member} is capped. EXP gain = {results->ExpGains[i]}. Preventing level up.");
                 //   }
                //    else if (gotNoExp && (&results->PersonaChanges)[i].LevelIncrease > 0)
                //    {
                 //       (&results->PersonaChanges)[i].LevelIncrease = 0;
                //        results->LevelUpStatus &= unchecked((ushort)~(1 << (i * 4)));
                //        _levelUps.Remove(member);
                //
                //        Utils.LogDebug($"{member} had LevelIncrease but 0 EXP — breaking false level-up.");
                //    }
                }



        private void GivePartyMemberExp(BattleResults* results, nuint param_2, nuint param_3, nuint param_4)
        {
            _givePartyMemberExpHook.OriginalFunction(results, param_2, param_3, param_4);

        }

        private nuint LevelUpPartyMember(BattleResultsThing* resultsThing)
        {
            var thing = resultsThing->Thing;

            if (thing->State > 1 || _levelUps.Count == 0)
            {
                return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
            }

            var results = &thing->Info->Results;

            Utils.LogDebug($"LevelUpSlot = {thing->LevelUpSlot}");
            for (int i = 0; i < 4; i++)
            {
                Utils.LogDebug($"Slot {i} is {(PartyMember)results->PartyMembers[i]} who has {(&results->PersonaChanges)[i].LevelIncrease} level increases and {results->ExpGains[i]} exp gained.");
            }
            bool hasActiveLevelUps = false;
            for (int i = thing->LevelUpSlot; i < 4; i++)
            {
                var curMember = results->PartyMembers[i];
                if (curMember == 0) continue;

                if ((&results->PersonaChanges)[i].LevelIncrease != 0 && results->ExpGains[i] > 0)
                {
                    hasActiveLevelUps = true;
                    break;
                }
                else
                {
                    results->PartyMembers[i] = 0;
                }
            }
            if (hasActiveLevelUps)
            {
                return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
            }

            if (thing->LevelUpSlot == 0)
            {
                for (int i = 1; i < 4; i++)
                    results->PartyMembers[i] = 0;

                var levelUp = _levelUps.First();
                var member = levelUp.Key;
                var statChanges = levelUp.Value;
                results->PartyMembers[0] = (short)member;
                results->ExpGains[0] = (uint)_expGains[member];
                results->PersonaChanges = statChanges;

                Utils.LogDebug($"Leveling up inactive member {member}");
                _levelUps.Remove(member);
                _expGains.Remove(member);

                return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
            }
            else
            {
                return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
            }
        }

        private delegate void SetupResultsExpDelegate(BattleResults* results, astruct_2* param_2);
        private delegate void GivePartyMemberExpDelegate(BattleResults* results, nuint param_2, nuint param_3, nuint param_4);
        private delegate nuint LevelUpPartyMemberDelegate(BattleResultsThing* results);

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            _configuration = configuration;
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}