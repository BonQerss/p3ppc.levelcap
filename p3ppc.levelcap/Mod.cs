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
using System.Reflection.Metadata.Ecma335;
using System.Linq;

namespace p3ppc.levelcap
{
    /// <summary>
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

        // Thread-safe collections with locks
        private readonly Dictionary<PartyMember, int> _expGains = new();
        private readonly Dictionary<PartyMember, PersonaStatChanges> _levelUps = new();
        private readonly object _dictionaryLock = new object();
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

        private delegate int GetLevelDelegate(PartyMember member);
        private GetLevelDelegate _getLevel;

        private delegate int CalculateLevelDelegate(int totalExp);
        private CalculateLevelDelegate _calculateLevel;

        private delegate int GetPartyMemberExpDelegate(PartyMember member, astruct_2* param_2);
        private GetPartyMemberExpDelegate _getPartyMemberExp;

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

        private bool _isProcessingLevelUps = false;

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

            Utils.SigScan("40 53 48 83 EC 20 89 CB 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 45 30 C0", "CalculateLevel", address =>
            {
                _calculateLevel = _hooks.CreateWrapper<CalculateLevelDelegate>(address, out _);
            });

            Utils.SigScan("E8 ?? ?? ?? ?? 0F B7 CB E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 75 ?? 8B 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? 83 C2 20", "GetLevel", address =>
            {
                var funcAddress = Utils.GetGlobalAddress((nint)(address + 1));
                _getLevel = _hooks.CreateWrapper<GetLevelDelegate>((long)funcAddress, out _);
                _logger.WriteLine($"Found GetLevel at 0x{address:X}");
            });

            Utils.SigScan("E8 ?? ?? ?? ?? 41 8B CF 89 44 24 ??", "GetPartyMemberExp", address =>
            {
                var funcAddress = Utils.GetGlobalAddress((nint)(address + 1));
                _getPartyMemberExp = _hooks.CreateWrapper<GetPartyMemberExpDelegate>((long)funcAddress, out _);
                _logger.WriteLine($"Found GetPartyMemberExp at 0x{funcAddress:X}");
            });
        }

        private int GetCurrentLevelCap()
        {
            try
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
                        _logger.WriteLine($"[GetCurrentLevelCap] Current day ({currentDay}) is before {key}, returning cap {cap}");
                        return cap;
                    }
                    previousKey = key;
                    previousCap = cap;
                }

                _logger.WriteLine($"[GetCurrentLevelCap] Current day >= last cap date ({previousKey}), returning last cap {previousCap}");
                return previousCap;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GetCurrentLevelCap] Error: {ex.Message}");
                return 99; // Safe fallback
            }
        }

        private int CalculateCappedExp(int currentLevel, int gainedExp, int levelCap, int requiredExp, int currentExp, int totalExpForLevelCap)
        {
            try
            {
                if (currentLevel >= levelCap)
                {
                    Utils.LogDebug($"Character already at level cap {levelCap} (current level: {currentLevel}). No EXP gain.");
                    return 0;
                }

                if (currentExp + gainedExp > totalExpForLevelCap)
                {
                    int cappedGain = Math.Max(0, totalExpForLevelCap - currentExp);
                    Utils.LogDebug($"Capping EXP: {cappedGain} instead of {gainedExp} (current: {currentExp}, cap total: {totalExpForLevelCap})");
                    return cappedGain;
                }

                Utils.LogDebug($"No capping needed: {gainedExp}");
                return gainedExp;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[CalculateCappedExp] Error: {ex.Message}");
                return 0; // Safe fallback
            }
        }

        private void SetupResultsExp(BattleResults* results, astruct_2* param_2)
        {
            try
            {
                // Validate pointers
                if (results == null || param_2 == null)
                {
                    _logger.WriteLine("[SetupResultsExp] Null pointer detected, calling original");
                    _setupExpHook.OriginalFunction(results, param_2);
                    return;
                }

                lock (_dictionaryLock)
                {
                    _expGains.Clear();
                    _levelUps.Clear();
                }

                fixed (short* party = &_available[0])
                {
                    _numAvailable = GetAvailableParty(party);
                }

                _setupExpHook.OriginalFunction(results, param_2);

                int levelCap = GetCurrentLevelCap();

                // Setup Protag Itself EXP
                int protagLevel = _getLevel(PartyMember.Protag);
                Utils.LogDebug($"Current Protag Level is {protagLevel}");
                int gainedProtagExp = (int)(CalculateGainedExp(protagLevel, param_2));
                Utils.LogDebug($"Current gainedProtagExp is {gainedProtagExp}");
                int currentProtagExp = _getPartyMemberExp(PartyMember.Protag, param_2);
                Utils.LogDebug($"Current currentProtagExp is {currentProtagExp}");

                int cappedProtagExp;

                if (protagLevel >= 99 || protagLevel >= levelCap)
                {
                    cappedProtagExp = 0;
                    Utils.LogDebug($"[SetupResultsExp] Protag at level cap ({protagLevel} >= {levelCap}), setting EXP to 0");
                    results->GainedExp = cappedProtagExp;
                }
                else
                {
                    int projectedLevel = _calculateLevel(currentProtagExp + gainedProtagExp);

                    if (projectedLevel > levelCap)
                    {
                        int maxExpForCapLevel = currentProtagExp;
                        for (int testExp = currentProtagExp; testExp <= currentProtagExp + gainedProtagExp; testExp++)
                        {
                            if (_calculateLevel(testExp) == levelCap)
                            {
                                maxExpForCapLevel = testExp;
                            }
                            else if (_calculateLevel(testExp) > levelCap)
                            {
                                break;
                            }
                        }

                        cappedProtagExp = Math.Max(0, maxExpForCapLevel - currentProtagExp);
                        Utils.LogDebug($"[SetupResultsExp] Protag would reach level {projectedLevel}, capping exp from {gainedProtagExp} to {cappedProtagExp} to stay at level {levelCap}");
                    }
                    else
                    {
                        cappedProtagExp = gainedProtagExp;
                        Utils.LogDebug($"[SetupResultsExp] Protag will reach level {projectedLevel}, no capping needed.");
                    }

                    results->GainedExp = cappedProtagExp;
                }

                int finalLevel = _calculateLevel(currentProtagExp + cappedProtagExp);
                if (protagLevel != finalLevel)
                {
                    results->LevelUpStatus = results->LevelUpStatus | 2;
                }

                // Setup Party Exp
                for (PartyMember member = PartyMember.Yukari; member <= PartyMember.Koromaru; member++)
                {
                    lock (_dictionaryLock)
                    {
                        _expGains.Remove(member);
                        _levelUps.Remove(member);
                    }

                    if (!IsActive(member, results)) continue;

                    var persona = GetPartyMemberPersona(member);
                    if (persona == null || persona == (Persona*)0)
                    {
                        Utils.LogDebug($"Skipping {member} - null persona");
                        continue;
                    }

                    var level = persona->Level;

                    int gainedExp = (int)(CalculateGainedExp(level, param_2));
                    var currentExp = persona->Exp;
                    var requiredExp = GetPersonaRequiredExp(persona, (ushort)(level + 1));
                    var requiredREALexp = GetPersonaRequiredExp(persona, (ushort)(levelCap));

                    int cappedExp;

                    if (level >= 99 || level >= levelCap)
                    {
                        cappedExp = 0;
                        Utils.LogDebug($"[SetupResultsExp] Member {member} at level cap ({level} >= {levelCap}), setting EXP to 0");

                        // CLEAR THE LEVEL-UP STATUS for this member
                        for (int slot = 0; slot < 4; slot++)
                        {
                            if (results->PartyMembers[slot] == (short)member)
                            {
                                results->ExpGains[slot] = 0;
                                (&results->PersonaChanges)[slot] = new PersonaStatChanges();
                                Utils.LogDebug($"[SetupResultsExp] Cleared level-up data for {member} at slot {slot}");
                                break;
                            }
                        }
                    }
                    else
                    {
                        cappedExp = CalculateCappedExp(level, gainedExp, levelCap, requiredExp, currentExp, requiredREALexp);

                        lock (_dictionaryLock)
                        {
                            _expGains[member] = cappedExp;
                        }

                        // Write capped EXP back to results array
                        for (int slot = 0; slot < 4; slot++)
                        {
                            if (results->PartyMembers[slot] == (short)member)
                            {
                                results->ExpGains[slot] = (uint)cappedExp;
                                Utils.LogDebug($"[SetupResultsExp] Member {member} level {level} - EXP clamped from {gainedExp} to {cappedExp} (cap: {levelCap})");
                                break;
                            }
                        }

                        // Only check for level up if we're giving EXP and not at cap
                        if (cappedExp > 0 && level < levelCap && requiredExp <= currentExp + cappedExp)
                        {
                            Utils.LogDebug($"{member} is ready to level up");
                            results->LevelUpStatus |= 0x10;
                            var statChanges = new PersonaStatChanges { };
                            GenerateLevelUpPersona(persona, &statChanges, cappedExp);

                            lock (_dictionaryLock)
                            {
                                _levelUps[member] = statChanges;
                            }
                        }
                    }
                }

                // Setup Protag Persona Exp
                var activePersona = GetPartyMemberPersona(PartyMember.Protag);
                if (activePersona != null && activePersona != (Persona*)0)
                {
                    for (short i = 0; i < 12; i++)
                    {
                        var persona = GetProtagPersona(i);
                        if (persona == null || persona == (Persona*)0) continue;

                        var level = persona->Level;

                        if (level >= 99 || level >= levelCap)
                        {
                            // Clear protag persona level-up data
                            results->ProtagExpGains[i] = 0;
                            (&results->ProtagPersonaChanges)[i] = new PersonaStatChanges();
                            Utils.LogDebug($"[SetupResultsExp] Cleared level-up data for Protag Persona {i} at level cap");
                        }
                        else
                        {
                            int gainedExp = (int)results->ProtagExpGains[i];
                            int finalExpGained = 0;

                            if (persona->Id == activePersona->Id)
                            {
                                finalExpGained = gainedExp;
                            }
                            else
                            {
                                var skills = _getPersonaSkills(new nint(persona));
                                if (skills == null)
                                {
                                    Utils.LogDebug($"Skipping Protag Persona {i} - null skills");
                                    continue;
                                }

                                bool hasGrowth1 = false;
                                bool hasGrowth2 = false;
                                bool hasGrowth3 = false;

                                for (int skillSlot = 0; skillSlot < 8; skillSlot++)
                                {
                                    short skill = skills[skillSlot];
                                    if (skill == 0x229) hasGrowth1 = true;
                                    else if (skill == 0x22a) hasGrowth2 = true;
                                    else if (skill == 0x22b) hasGrowth3 = true;
                                }

                                if (hasGrowth3) finalExpGained = gainedExp;
                                else if (hasGrowth2) finalExpGained = (int)(gainedExp * 0.5f);
                                else if (hasGrowth1) finalExpGained = (int)(gainedExp * 0.25f);
                                else continue;
                            }

                            var currentExp = persona->Exp;
                            var requiredExp = GetPersonaRequiredExp(persona, (ushort)(level + 1));
                            var requiredREALexp = GetPersonaRequiredExp(persona, (ushort)levelCap);

                            int cappedExp = CalculateCappedExp(level, finalExpGained, levelCap, requiredExp, currentExp, requiredREALexp);

                            results->ProtagExpGains[i] = (uint)cappedExp;
                            Utils.LogDebug($"[SetupResultsExp] Protag Persona {i} ({persona->Id}) level {level} - EXP clamped from {finalExpGained} to {cappedExp} (cap: {levelCap})");

                            if (cappedExp > 0 && requiredExp <= currentExp + cappedExp)
                            {
                                Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is ready to level up");
                                results->LevelUpStatus |= 8;
                                GenerateLevelUpPersona(persona, &(&results->ProtagPersonaChanges)[i], cappedExp);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[SetupResultsExp] Critical error: {ex.Message}");
                // Call original to maintain game stability
                _setupExpHook.OriginalFunction(results, param_2);
            }
        }

        private void GivePartyMemberExp(BattleResults* results, nuint param_2, nuint param_3, nuint param_4)
        {
            try
            {
                // Validate pointer
                if (results == null)
                {
                    _logger.WriteLine("[GivePartyMemberExp] Null results pointer");
                    _givePartyMemberExpHook.OriginalFunction(results, param_2, param_3, param_4);
                    return;
                }

                _givePartyMemberExpHook.OriginalFunction(results, param_2, param_3, param_4);

                for (PartyMember member = PartyMember.Yukari; member <= PartyMember.Koromaru; member++)
                {
                    bool hasExpGain;
                    int expGained;

                    lock (_dictionaryLock)
                    {
                        hasExpGain = _expGains.TryGetValue(member, out expGained);
                    }

                    if (!IsActive(member, results) || !hasExpGain) continue;

                    var persona = GetPartyMemberPersona(member);
                    if (persona == null || persona == (Persona*)0)
                    {
                        Utils.LogDebug($"Skipping exp gain for {member} - null persona");
                        continue;
                    }

                    persona->Exp += expGained;
                    Utils.LogDebug($"Gave {expGained} exp to {member}");

                    if (CanPersonaLevelUp(persona, (nuint)expGained, param_3, param_4) != 0)
                    {
                        PersonaStatChanges statChanges;
                        lock (_dictionaryLock)
                        {
                            if (_levelUps.TryGetValue(member, out statChanges))
                            {
                                Utils.LogError($"Levelling up {member} without menu display, this shouldn't happen!");
                                LevelUpPersona(persona, &statChanges);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GivePartyMemberExp] Error: {ex.Message}");
                // Still call original to maintain stability
                _givePartyMemberExpHook.OriginalFunction(results, param_2, param_3, param_4);
            }
        }

        private bool IsActive(PartyMember member, BattleResults* results)
        {
            try
            {
                if (results == null) return false;

                // Check if they're already in the party
                for (int i = 0; i < 4; i++)
                {
                    if (results->PartyMembers[i] == (short)member) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[IsActive] Error checking member {member}: {ex.Message}");
                return false;
            }
        }

        private nuint LevelUpPartyMember(BattleResultsThing* resultsThing)
        {
            try
            {
                // Validate pointer
                if (resultsThing == null || resultsThing->Thing == null)
                {
                    _logger.WriteLine("[LevelUpPartyMember] Null pointer detected");
                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }

                // Prevent recursion
                if (_isProcessingLevelUps)
                {
                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }

                var thing = resultsThing->Thing;
                var results = &thing->Info->Results;

                // We only want to change stuff in state 1
                if (thing->State > 1)
                {
                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }

                for (int i = 0; i < 4; i++)
                {
                    if (results->PartyMembers[i] != 0)
                    {
                        var member = (PartyMember)results->PartyMembers[i];
                        int expectedExp;
                        lock (_dictionaryLock)
                        {
                            _expGains.TryGetValue(member, out expectedExp);
                        }
                        Utils.LogDebug($"  {member}: {results->ExpGains[i]} EXP (expected from _expGains: {expectedExp})");
                    }
                }

                Utils.LogDebug($"LevelUpSlot = {thing->LevelUpSlot}");
                for (int i = 0; i < 4; i++)
                {
                    Utils.LogDebug($"Slot {i} is {(PartyMember)results->PartyMembers[i]} who has {(&results->PersonaChanges)[i].LevelIncrease} level increases and {results->ExpGains[i]} exp gained.");
                }

                // Check if we have custom level-ups to process
                bool hasCustomLevelUps;
                lock (_dictionaryLock)
                {
                    hasCustomLevelUps = _levelUps.Count > 0;
                }

                if (!hasCustomLevelUps)
                {
                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }

                if (thing->LevelUpSlot < 4)
                {
                    var currentSlot = thing->LevelUpSlot;
                    var currentMember = results->PartyMembers[currentSlot];

                    if (currentMember != 0 && (&results->PersonaChanges)[currentSlot].LevelIncrease > 0)
                    {
                        lock (_dictionaryLock)
                        {
                            _levelUps.Remove((PartyMember)currentMember);
                            _expGains.Remove((PartyMember)currentMember);
                        }
                    }

                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }

                // Process our custom level-ups
                KeyValuePair<PartyMember, PersonaStatChanges>? levelUpPair = null;
                int expGained = 0;

                lock (_dictionaryLock)
                {
                    if (_levelUps.Count > 0)
                    {
                        var levelUp = _levelUps.First();
                        levelUpPair = levelUp;
                        _expGains.TryGetValue(levelUp.Key, out expGained);
                    }
                }

                if (levelUpPair.HasValue)
                {
                    var member = levelUpPair.Value.Key;
                    var statChanges = levelUpPair.Value.Value;

                    // Bounds check before array access
                    if (results->PartyMembers != null)
                    {
                        // Use slot 0 for our custom level-up
                        results->PartyMembers[0] = (short)member;
                        results->ExpGains[0] = (uint)expGained;
                        (&results->PersonaChanges)[0] = statChanges;

                        // Clear other slots to prevent interference
                        for (int i = 1; i < 4; i++)
                        {
                            results->PartyMembers[i] = 0;
                            (&results->PersonaChanges)[i] = new PersonaStatChanges();
                            results->ExpGains[i] = 0;
                        }
                    }

                    // Reset to process this level-up
                    thing->LevelUpSlot = 0;

                    Utils.LogDebug($"Leveling up {member}");

                    lock (_dictionaryLock)
                    {
                        _levelUps.Remove(member);
                        _expGains.Remove(member);
                    }

                    _isProcessingLevelUps = true;
                    var result = _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                    _isProcessingLevelUps = false;
                    return result;
                }

                // No more level-ups to process
                return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[LevelUpPartyMember] Critical error: {ex.Message}");
                _isProcessingLevelUps = false; // Reset flag on error
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