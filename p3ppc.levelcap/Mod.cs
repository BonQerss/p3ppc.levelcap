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

        // Function delegates - based on actual assembly analysis
        private delegate void GiveProtagExpDelegate(IntPtr results, IntPtr param2, IntPtr param3, IntPtr param4);
        private delegate void GivePersonaExpDelegate(IntPtr persona, uint exp);
        private delegate IntPtr GetProtagPersonaDelegate(short slot);
        private delegate IntPtr GetPartyMemberPersonaDelegate(IntPtr partyMemberInfo);
        private delegate byte GetPersonaLevelDelegate(IntPtr persona);
        private delegate byte GetPartyMemberLevelDelegate(IntPtr partyMemberInfo);
        private delegate ushort GetNumPersonasDelegate();
        private delegate short* GetPersonaSkillsDelegate(IntPtr persona);

        // Hooks
        private IHook<GiveProtagExpDelegate> _giveProtagExpHook;
        private IHook<GivePersonaExpDelegate> _givePersonaExpHook;

        // Function wrappers - based on actual assembly
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

        }

        private int GetCurrentLevelCap()
        {
            int currentDay = 0;
            currentDay = _getTotalDay();
            _logger.WriteLine($"[GetCurrentLevelCap] Current day from _getTotalDay: 0x{currentDay:X}");

            int maxLevel = 1;
            foreach (var kvp in _levelCaps)
            {
                if (currentDay >= kvp.Key)
                {
                    maxLevel = kvp.Value;
                }
                else
                {
                    break;
                }
            }

            _logger.WriteLine($"[GetCurrentLevelCap] Returning max level cap: {maxLevel}");
            return maxLevel;
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

        private bool IsPartyMemberAtLevelCap(IntPtr partyMemberInfo)
        {
            byte currentLevel = 0;
            currentLevel = _getPartyMemberLevel(partyMemberInfo);
            _logger.WriteLine($"[IsPartyMemberAtLevelCap] Party member current level: {currentLevel}");
            int levelCap = GetCurrentLevelCap();
            bool atCap = currentLevel >= levelCap;
            _logger.WriteLine($"[IsPartyMemberAtLevelCap] Level cap: {levelCap}, At cap: {atCap}");
            return atCap;
        }

        private int CalculateCappedExp(Persona* persona, int gainedExp, int levelCap)
        {
            var currentLevel = persona->Level;
            var currentExp = persona->Exp;

            if (currentLevel >= levelCap)
            {
                // Already at or above cap, no EXP gain
                return 0;
            }

            // Calculate what the new EXP total would be
            int newExpTotal = currentExp + gainedExp;

            // Get the EXP required to reach the level cap
            var expRequiredForCap = GetPersonaRequiredExp(persona, (ushort)levelCap);

            // Safety check for GetPersonaRequiredExp returning 0
            if (expRequiredForCap == 0 || expRequiredForCap <= currentExp)
            {
                Utils.LogDebug($"Invalid EXP requirement detected: Required {expRequiredForCap}, Current {currentExp}. Setting gain to 0.");
                return 0;
            }

            // If the new EXP total would exceed what's needed for the cap,
            // reduce the gained EXP to only reach the cap
            if (newExpTotal > expRequiredForCap)
            {
                int cappedGainedExp = expRequiredForCap - currentExp;
                Utils.LogDebug($"EXP overflow detected: Original gain {gainedExp}, capped to {cappedGainedExp} to reach level cap {levelCap}");
                return Math.Max(0, cappedGainedExp); // Ensure we don't return negative values
            }

            // No overflow, return original gained EXP
            return gainedExp;
        }
        

        private bool ShouldPersonaReceiveExp(short personaSlot, Persona* activePersona)
        {
            var persona = GetProtagPersona(personaSlot);
            if (persona == (Persona*)0) return false;

            // Active persona always receives full EXP
            if (persona->Id == activePersona->Id)
            {
                Utils.LogDebug($"Persona slot {personaSlot} is active persona, will receive full EXP");
                return true;
            }

            Utils.LogDebug($"Persona slot {personaSlot} will not receive EXP (not active, no Growth skills)");
            return false;
        }
        private void SetupResultsExp(BattleResults* results, astruct_2* param_2)
        {
            // Let the original function do ALL the work first - it already handles:
            // - Growth skill detection and EXP calculation
            // - Setting up results->ProtagExpGains[]
            // - Setting up results->ExpGains[] for party members
            // - Level up detection and stat generation
            _setupExpHook.OriginalFunction(results, param_2);

            int levelCap = GetCurrentLevelCap();

            // Now just apply level caps to the results the game already calculated

            // Cap protagonist persona EXP gains
            for (short i = 0; i < 12; i++)
            {
                if (results->ProtagExpGains[i] == 0) continue; // Skip if no EXP was given

                var persona = GetProtagPersona(i);
                if (persona == (Persona*)0) continue;

                var level = persona->Level;
                if (level >= levelCap)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is at level cap ({level} >= {levelCap}), removing EXP gain");
                    results->ProtagExpGains[i] = 0;
                    continue;
                }

                // Cap the EXP to not exceed level cap
                int originalExp = (int)results->ProtagExpGains[i];
                int cappedExp = CalculateCappedExp(persona, originalExp, levelCap);

                if (cappedExp != originalExp)
                {
                    Utils.LogDebug($"Capped Protag Persona {i} EXP from {originalExp} to {cappedExp}");
                    results->ProtagExpGains[i] = (uint)cappedExp;
                }
            }

            // Cap party member EXP gains
            for (int i = 0; i < 4; i++) // Max 4 party members in results
            {
                if (results->PartyMembers[i] == 0) continue; // Empty slot
                if (results->ExpGains[i] == 0) continue; // No EXP gain

                var member = (PartyMember)results->PartyMembers[i];
                var persona = GetPartyMemberPersona(member);
                if (persona == (Persona*)0) continue;

                var level = persona->Level;
                if (level >= levelCap)
                {
                    Utils.LogDebug($"Party member {member} is at level cap ({level} >= {levelCap}), removing EXP gain");
                    results->ExpGains[i] = 0;

                    // Also clear level up flag if it was set
                    if ((results->LevelUpStatus & 0x10) != 0)
                    {
                        // You might need to be more careful here about which specific member was going to level up
                        Utils.LogDebug($"Clearing level up status for capped member {member}");
                    }
                    continue;
                }

                // Cap the EXP to not exceed level cap
                int originalExp = (int)results->ExpGains[i];
                int cappedExp = CalculateCappedExp(persona, originalExp, levelCap);

                if (cappedExp != originalExp)
                {
                    Utils.LogDebug($"Capped {member} EXP from {originalExp} to {cappedExp}");
                    results->ExpGains[i] = (uint)cappedExp;
                }
            }
        }

        private bool IsInactive(PartyMember member, BattleResults* results)
        {
            // Check if they're already in the party
            for (int i = 0; i < 4; i++)
            {
                if (results->PartyMembers[i] == (short)member) return false;
            }

            // Check if they're available
            for (int i = 0; i < _numAvailable; i++)
            {
                if (_available[i] == (short)member) return true;
            }
            return false;
        }

        private void GivePartyMemberExp(BattleResults* results, nuint param_2, nuint param_3, nuint param_4)
        {
            _givePartyMemberExpHook.OriginalFunction(results, param_2, param_3, param_4);

            for (PartyMember member = PartyMember.Yukari; member <= PartyMember.Koromaru; member++)
            {
                if (!IsInactive(member, results) || !_expGains.ContainsKey(member)) continue;

                var persona = GetPartyMemberPersona(member);
                var expGained = _expGains[member]; // This is already capped from SetupResultsExp

                // Just add the EXP and let the game handle level ups naturally
                if (expGained > 0)
                {
                    var oldLevel = persona->Level;
                    persona->Exp += expGained;

                    // Check if they leveled up and handle level cap enforcement
                    var newLevel = persona->Level;
                    int levelCap = GetCurrentLevelCap();

                    if (newLevel > levelCap)
                    {
                        // Force level back to cap and adjust EXP accordingly
                        persona->Level = (byte)levelCap;
                        var expForCap = GetPersonaRequiredExp(persona, (ushort)levelCap);
                        persona->Exp = expForCap;
                        Utils.LogDebug($"Capped {member} level from {newLevel} to {levelCap}");
                    }

                    Utils.LogDebug($"Gave {expGained} exp to {member}, level {oldLevel} -> {persona->Level}");
                }
            }
        }

        // NEW: Level up hook to handle level caps during level ups
        private nuint LevelUpPartyMember(BattleResultsThing* resultsThing)
        {
            var thing = resultsThing->Thing;

            // We only want to change stuff in state 1 (when it's picking a Persona's level up stuff to deal with)
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

            // Wait until all of the real level ups have been done so we can safely overwrite their data
            for (int i = thing->LevelUpSlot; i < 4; i++)
            {
                var curMember = results->PartyMembers[i];
                if (curMember == 0) continue;

                if ((&results->PersonaChanges)[i].LevelIncrease != 0)
                {
                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }
                else
                {
                    // Give them the exp ourself with level cap enforcement
                    var persona = GetPartyMemberPersona((PartyMember)curMember);
                    int levelCap = GetCurrentLevelCap();

                    if (persona->Level < levelCap)
                    {
                        int expToAdd = (int)results->ExpGains[i];
                        int cappedExpToAdd = CalculateCappedExp(persona, expToAdd, levelCap);
                        persona->Exp += cappedExpToAdd;

                        // Enforce level cap if they went over
                        if (persona->Level > levelCap)
                        {
                            Utils.LogDebug($"Enforcing level cap during EXP addition: {(PartyMember)curMember} level {persona->Level} -> {levelCap}");
                            persona->Level = (byte)levelCap;
                            var expForCap = GetPersonaRequiredExp(persona, (ushort)levelCap);
                            persona->Exp = expForCap;
                        }

                        Utils.LogDebug($"Gave {cappedExpToAdd} capped EXP to {(PartyMember)curMember} (original: {expToAdd})");
                    }
                    else
                    {
                        Utils.LogDebug($"Skipping EXP for {(PartyMember)curMember} - already at level cap");
                    }

                    results->PartyMembers[i] = 0;
                }
            }

            // Clear all of the real level ups so they can't loop
            for (int i = 1; i < 4; i++)
                results->PartyMembers[i] = 0;

            // Change the data of an active party member to an inactive one
            thing->LevelUpSlot = 0;
            var levelUp = _levelUps.First();
            var member = levelUp.Key;
            var statChanges = levelUp.Value;

            // Apply level cap enforcement to the level up data
            int currentLevelCap = GetCurrentLevelCap();
            var memberPersona = GetPartyMemberPersona(member);

            if (memberPersona->Level >= currentLevelCap)
            {
                Utils.LogDebug($"Skipping level up for {member} - already at level cap ({memberPersona->Level} >= {currentLevelCap})");
                _levelUps.Remove(member);
                _expGains.Remove(member);

                // If there are more level ups to process, recursively call this function
                if (_levelUps.Count > 0)
                {
                    return LevelUpPartyMember(resultsThing);
                }
                else
                {
                    return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
                }
            }

            results->PartyMembers[0] = (short)member;
            results->ExpGains[0] = (uint)_expGains[member];
            results->PersonaChanges = statChanges;

            Utils.LogDebug($"Leveling up {member}");
            _levelUps.Remove(member);
            _expGains.Remove(member);

            return _levelUpPartyMemberHook.OriginalFunction(resultsThing);
        }

        private delegate void SetupResultsExpDelegate(BattleResults* results, astruct_2* param_2);
        private delegate void GivePartyMemberExpDelegate(BattleResults* results, nuint param_2, nuint param_3, nuint param_4);
        private delegate nuint LevelUpPartyMemberDelegate(BattleResultsThing* results);

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            // Apply settings from configuration.
            // ... your code here.
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