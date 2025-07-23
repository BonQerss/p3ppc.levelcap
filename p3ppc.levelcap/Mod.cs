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

            Utils.SigScan("E8 ?? ?? ?? ?? 45 33 D2 4C 8B D8", "GetPersonaSkills", address =>
            {
                var funcAddress = Utils.GetGlobalAddress((nint)(address + 1));
                _getPersonaSkills = _hooks.CreateWrapper<GetPersonaSkillsDelegate>((long)funcAddress, out _);
                _logger.WriteLine($"Found GetPersonaSkills at 0x{address:X}");
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


        private void SetupResultsExp(BattleResults* results, astruct_2* param_2)
        {
            // Essential: Keep the fixed block and original function call
            fixed (short* party = &_available[0])
            {
                _numAvailable = GetAvailableParty(party);
            }
            _setupExpHook.OriginalFunction(results, param_2);

            // Setup Party Exp
            for (PartyMember member = PartyMember.Yukari; member <= PartyMember.Koromaru; member++)
            {
                _expGains.Remove(member);
                _levelUps.Remove(member);
                if (IsInactive(member, results)) continue;

                var persona = GetPartyMemberPersona(member);
                var level = persona->Level;
                int levelCap = GetCurrentLevelCap();
                if (level >= 99 || level >= levelCap)
                {
                    Utils.LogDebug($"{member} is above or at level cap ({level} >= {levelCap}), skipping EXP gain.");
                    continue;
                }

                int gainedExp = (int)(CalculateGainedExp(level, param_2));

                if (gainedExp > 0)
                {
                    int cappedExp = CalculateCappedExp(persona, gainedExp, GetCurrentLevelCap());
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

                var currentExp = persona->Exp;
                var requiredExp = GetPersonaRequiredExp(persona, (ushort)(level + 1));
                _expGains[member] = gainedExp;
                if (requiredExp <= currentExp + gainedExp)
                {
                    Utils.LogDebug($"{member} is ready to level up");
                    results->LevelUpStatus |= 0x10; // signify that a party member is ready to level up
                    var statChanges = new PersonaStatChanges { };
                    GenerateLevelUpPersona(persona, &statChanges, gainedExp);
                    _levelUps[member] = statChanges;
                }
            }

            // Setup Protag Persona Exp - Modified to only affect active persona and Growth skill personas
            var activePersona = GetPartyMemberPersona(PartyMember.Protag);
            if (activePersona == null)
            {
                Utils.LogError("Failed to get active persona for protagonist!");
                return;
            }

            short activePersonaId = activePersona->Id;
            int levelCapForProtag = GetCurrentLevelCap();

            for (short i = 0; i < 12; i++)
            {
                var persona = GetProtagPersona(i);
                if (persona == (Persona*)0)
                    continue;

                var level = persona->Level;
                if (level >= 99 || level >= levelCapForProtag)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is above or at level cap ({level} >= {levelCapForProtag}), skipping EXP gain.");
                    continue;
                }

                int baseExpGained = (int)(CalculateGainedExp(level, param_2));
                uint finalExpGained = (uint)baseExpGained;

                // Check if this is the active persona or has growth skills
                if (persona->Id != activePersonaId)
                {
                    // Check if _getPersonaSkills is initialized
                    if (_getPersonaSkills == null)
                    {
                        Utils.LogError("_getPersonaSkills is not initialized!");
                        continue;
                    }

                    var skills = _getPersonaSkills(new IntPtr(persona));
                    if (skills == null)
                    {
                        Utils.LogDebug($"Failed to get skills for Protag Persona {i} ({persona->Id}), skipping.");
                        continue;
                    }

                    uint growth1Gains = 0;
                    uint growth2Gains = 0;
                    uint growth3Gains = 0;

                    // Check all 8 skill slots for Growth skills
                    for (int skillSlot = 0; skillSlot < 8; skillSlot++)
                    {
                        short skill = skills[skillSlot];
                        if (skill != 0)
                        {
                            if (skill == 0x229) // Growth 1
                            {
                                growth1Gains = (uint)(baseExpGained * 0.25f);
                            }
                            else if (skill == 0x22a) // Growth 2
                            {
                                growth2Gains = (uint)(baseExpGained * 0.5f);
                            }
                            else if (skill == 0x22b) // Growth 3
                            {
                                growth3Gains = (uint)baseExpGained;
                            }
                        }
                    }

                    // Priority system: Growth 3 > Growth 2 > Growth 1 > None
                    if (growth3Gains >= 1)
                    {
                        finalExpGained = growth3Gains;
                    }
                    else if (growth2Gains >= 1)
                    {
                        finalExpGained = growth2Gains;
                    }
                    else if (growth1Gains >= 1)
                    {
                        finalExpGained = growth1Gains;
                    }
                    else
                    {
                        // No growth skills, skip this persona
                        Utils.LogDebug($"Protag Persona {i} ({persona->Id}) has no Growth skills and is not active, skipping EXP gain.");
                        continue;
                    }
                }

                // Apply level cap to the final EXP
                int cappedExp = CalculateCappedExp(persona, (int)finalExpGained, levelCapForProtag);

                if (cappedExp != (int)finalExpGained)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) EXP capped from {finalExpGained} to {cappedExp}.");
                }

                if (cappedExp == 0)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is capped — no EXP will be applied.");
                    continue;
                }

                finalExpGained = (uint)cappedExp;
                results->ProtagExpGains[i] += finalExpGained;

                string reasonText = (persona->Id == activePersonaId) ? "active persona" : "has Growth skill";
                Utils.LogDebug($"Giving Protag Persona {i} ({persona->Id}) {finalExpGained} exp ({reasonText})");

                var currentExp = persona->Exp;
                var requiredExp = GetPersonaRequiredExp(persona, (ushort)(level + 1));
                if (requiredExp <= currentExp + (int)finalExpGained)
                {
                    Utils.LogDebug($"Protag Persona {i} ({persona->Id}) is ready to level up");
                    results->LevelUpStatus |= 8; // signify that a protag persona is ready to level up
                    GenerateLevelUpPersona(persona, &(&results->ProtagPersonaChanges)[i], (int)finalExpGained);
                }
            }
        }

        private bool IsInactive(PartyMember member, BattleResults* results)
        {
            // Check if they're already in the party
            for (int i = 0; i < 4; i++)
            {
                if (results->PartyMembers[i] == (short)member) return false;
            };

            return true;
        }

        private void GivePartyMemberExp(BattleResults* results, nuint param_2, nuint param_3, nuint param_4)
        {
            _givePartyMemberExpHook.OriginalFunction(results, param_2, param_3, param_4);

            for (PartyMember member = PartyMember.Yukari; member <= PartyMember.Koromaru; member++)
            {
                if (IsInactive(member, results) || !_expGains.ContainsKey(member)) continue;

                var persona = GetPartyMemberPersona(member);
                var expGained = _expGains[member];

                if (IsPartyMemberAtLevelCap(new IntPtr(persona)))
                {
                    Utils.LogDebug($"{member} is at/above level cap, zeroing EXP gain.");
                    expGained = 0;
                }
                else
                {
                    persona->Exp += expGained;
                    Utils.LogDebug($"Gave {expGained} exp to {member}");
                }

                if (CanPersonaLevelUp(persona, (nuint)expGained, param_3, param_4) != 0)
                {
                    var statChanges = _levelUps[member];
                    Utils.LogError($"Levelling up {member} without menu display, this shouldn't happen!");
                    LevelUpPersona(persona, &statChanges);
                }
            }
        }

        private nuint LevelUpPartyMember(BattleResultsThing* resultsThing)
        {
            var thing = resultsThing->Thing;
            // Give exp to any inactive members that didn't level up
            if (_levelUps.Count == 0 && _expGains.Count > 0)
            {
                foreach (var (partyMember, expGained) in _expGains.ToList())
                {
                    // Skip inactive members
                    var persona = GetPartyMemberPersona(partyMember);
                    var dummyResults = stackalloc BattleResults[1]; // dummy for check
                    if (IsInactive(partyMember, dummyResults)) continue;

                    persona->Exp += expGained;
                    _expGains.Remove(partyMember);
                    Utils.LogDebug($"Gave {expGained} exp to {partyMember}");
                }
            }


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
                    // Give them the exp ourself and clear them to ensure they get it exactly once
                    GetPartyMemberPersona((PartyMember)curMember)->Exp += (int)results->ExpGains[i];
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