using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using p3ppc.levelcap.Template;
using Reloaded.Hooks.ReloadedII.Interfaces;
using p3ppc.levelcap.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace p3ppc.levelcap
{
    public unsafe class Mod : ModBase
    {
        // Function delegates - based on actual assembly analysis
        private delegate void GiveProtagExpDelegate(IntPtr results, IntPtr param2, IntPtr param3, IntPtr param4);
        private delegate void GivePartyMemberExpDelegate(IntPtr results, IntPtr param2, IntPtr param3, IntPtr param4);
        private delegate void GivePersonaExpDelegate(IntPtr persona, uint exp);
        private delegate int GetTotalDayDelegate();
        private delegate IntPtr GetProtagPersonaDelegate(short slot);
        private delegate IntPtr GetPartyMemberPersonaDelegate(IntPtr partyMemberInfo);
        private delegate byte GetPersonaLevelDelegate(IntPtr persona);
        private delegate byte GetPartyMemberLevelDelegate(IntPtr partyMemberInfo);
        private delegate ushort GetNumPersonasDelegate();

        // Hooks
        private IHook<GiveProtagExpDelegate> _giveProtagExpHook;
        private IHook<GivePartyMemberExpDelegate> _givePartyMemberExpHook;
        private IHook<GivePersonaExpDelegate> _givePersonaExpHook;

        // Function wrappers - based on actual assembly
        private GetTotalDayDelegate _getTotalDay;
        private GetProtagPersonaDelegate _getProtagPersona;
        private GetPartyMemberPersonaDelegate _getPartyMemberPersona;
        private GetPersonaLevelDelegate _getPersonaLevel;
        private GetPartyMemberLevelDelegate _getPartyMemberLevel;
        private GetNumPersonasDelegate _getNumPersonas;

        private readonly IModLoader _modLoader;
        private readonly Reloaded.Hooks.Definitions.IReloadedHooks _hooks;
        private readonly ILogger _logger;
        private readonly IMod _owner;
        private Config _configuration;
        private readonly IModConfig _modConfig;

        // Level cap configuration based on dates (day value -> max level)
        // Updated to use correct date progression
        private readonly SortedDictionary<int, int> _levelCaps = new SortedDictionary<int, int>
        {
            { 0x26, 8 },   // May 9th
            { 0x44, 15 },   // June 8th
            { 0x61, 21 },   // July 7th 
            { 0x79, 32 },  // August 6th
            { 0x9d, 40 },  // September 5th
            { 0x8F, 46 },  // October 4th
            { 0xE1, 54 },  // November 3rd
            { 0xF4, 54 },  // November 22nd
            { 0x131, 76 }   // July 31st
        };

        // Track if we're currently giving exp to prevent infinite recursion
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

            Utils.Initialise(_logger, _configuration, _modLoader);

            InitializeHooks();
        }

        private void InitializeHooks()
        {
            // Hook GiveProtagExp function
            Utils.SigScan("40 53 57 41 57 48 83 EC 20", "GiveProtagExp", address =>
            {
                _giveProtagExpHook = _hooks.CreateHook<GiveProtagExpDelegate>(GiveProtagExpHandler, address).Activate();
                _logger.WriteLine($"Hooked GiveProtagExp at 0x{address:X}");
            });

            // Hook GivePartyMemberExp function
            Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 83 EC 20 48 89 CF 48 8D 0D ?? ?? ?? ??", "GivePartyMemberExp", address =>
            {
                _givePartyMemberExpHook = _hooks.CreateHook<GivePartyMemberExpDelegate>(GivePartyMemberExpHandler, address).Activate();
                _logger.WriteLine($"Hooked GivePartyMemberExp at 0x{address:X}");
            });

            // Hook GivePersonaExp function - actual signature from assembly
            Utils.SigScan("48 89 5C 24 ?? 57 48 83 EC 20 48 89 CB 89 D7 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 85 FF 79 ??", "GivePersonaExp", address =>
            {
                _givePersonaExpHook = _hooks.CreateHook<GivePersonaExpDelegate>(GivePersonaExpHandler, address).Activate();
                _logger.WriteLine($"Hooked GivePersonaExp at 0x{address:X}");
            });

            // Get GetPersonaLevel function
            Utils.SigScan("40 53 48 83 EC 20 48 89 CB 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 43 ?? 48 83 C4 20", "GetPersonaLevel", address =>
            {
                _getPersonaLevel = _hooks.CreateWrapper<GetPersonaLevelDelegate>(address, out _);
                _logger.WriteLine($"Found GetPersonaLevel at 0x{address:X}");
            });

            // Get GetPartyMemberLevel function
            Utils.SigScan("40 53 48 83 EC 20 48 89 CB 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? F6 03 04 75 ??", "GetPartyMemberLevel", address =>
            {
                _getPartyMemberLevel = _hooks.CreateWrapper<GetPartyMemberLevelDelegate>(address, out _);
                _logger.WriteLine($"Found GetPartyMemberLevel at 0x{address:X}");
            });

            // Get GetTotalDay function
            Utils.SigScan("E8 ?? ?? ?? ?? 0F BF C8 89 0D ?? ?? ?? ?? 89 74 24 ??", "GetTotalDay", address =>
            {
                var funcAddress = Utils.GetGlobalAddress((nint)(address + 1));
                _getTotalDay = _hooks.CreateWrapper<GetTotalDayDelegate>((long)funcAddress, out _);
                _logger.WriteLine($"Found GetTotalDay at 0x{funcAddress:X}");
            });

            // Get GetProtagPersona function
            Utils.SigScan("40 53 48 83 EC 20 0F B7 D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 66 85 DB 78 ?? E8 ?? ?? ?? ?? 0F B7 D0 0F BF C3 39 D0 7C ?? 8B 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? FF C2 E8 ?? ?? ?? ?? 48 0F BF C3 48 6B C8 34 48 8D 05 ?? ?? ?? ?? 48 01 C8", "GetProtagPersona", address =>
            {
                _getProtagPersona = _hooks.CreateWrapper<GetProtagPersonaDelegate>(address, out _);
                _logger.WriteLine($"Found GetProtagPersona at 0x{address:X}");
            });

            // Get GetPartyMemberPersona function
            Utils.SigScan("40 53 48 83 EC 20 0F B7 D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 66 83 FB 01 75 ?? 0F B7 0D ?? ?? ?? ??", "GetPartyMemberPersona Call", address =>
            {
                _getPartyMemberPersona = _hooks.CreateWrapper<GetPartyMemberPersonaDelegate>(address, out _);
                _logger.WriteLine($"Found GetPartyMemberPersona at 0x{address:X}");
            });

            // Get GetNumPersonas function
            Utils.SigScan("40 53 48 83 EC 20 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 31 DB E8 ?? ?? ?? ??", "GetNumPersonas", address =>
            {
                _getNumPersonas = _hooks.CreateWrapper<GetNumPersonasDelegate>(address, out _);
                _logger.WriteLine($"Found GetNumPersonas at 0x{address:X}");
            });
        }

        private int GetCurrentLevelCap()
        {
            if (_getTotalDay == null)
            {
                _logger.WriteLine("[GetCurrentLevelCap] _getTotalDay delegate is null. Returning 99 (no cap).");
                return 99;
            }

            int currentDay = 0;
            try
            {
                currentDay = _getTotalDay();
                _logger.WriteLine($"[GetCurrentLevelCap] Current day from _getTotalDay: 0x{currentDay:X}");
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GetCurrentLevelCap] Exception calling _getTotalDay: {ex.Message}");
                return 99;
            }

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
            if (persona == IntPtr.Zero)
            {
                _logger.WriteLine("[IsPersonaAtLevelCap] persona pointer is zero");
                return false;
            }

            if (_getPersonaLevel == null)
            {
                _logger.WriteLine("[IsPersonaAtLevelCap] _getPersonaLevel delegate is null");
                return false;
            }

            byte currentLevel = 0;
            try
            {
                currentLevel = _getPersonaLevel(persona);
                _logger.WriteLine($"[IsPersonaAtLevelCap] Persona current level: {currentLevel}");
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[IsPersonaAtLevelCap] Exception calling _getPersonaLevel: {ex.Message}");
                return false;
            }

            int levelCap = GetCurrentLevelCap();
            bool atCap = currentLevel >= levelCap;
            _logger.WriteLine($"[IsPersonaAtLevelCap] Level cap: {levelCap}, At cap: {atCap}");
            return atCap;
        }


        private bool IsPartyMemberAtLevelCap(IntPtr partyMemberInfo)
        {
            if (partyMemberInfo == IntPtr.Zero)
            {
                _logger.WriteLine("[IsPartyMemberAtLevelCap] partyMemberInfo pointer is zero");
                return false;
            }

            if (_getPartyMemberLevel == null)
            {
                _logger.WriteLine("[IsPartyMemberAtLevelCap] _getPartyMemberLevel delegate is null");
                return false;
            }

            byte currentLevel = 0;
            try
            {
                currentLevel = _getPartyMemberLevel(partyMemberInfo);
                _logger.WriteLine($"[IsPartyMemberAtLevelCap] Party member current level: {currentLevel}");
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[IsPartyMemberAtLevelCap] Exception calling _getPartyMemberLevel: {ex.Message}");
                return false;
            }

            int levelCap = GetCurrentLevelCap();
            bool atCap = currentLevel >= levelCap;
            _logger.WriteLine($"[IsPartyMemberAtLevelCap] Level cap: {levelCap}, At cap: {atCap}");
            return atCap;
        }

        private void GiveProtagExpHandler(IntPtr results, IntPtr param2, IntPtr param3, IntPtr param4)
        {
            _logger.WriteLine("[GiveProtagExpHandler] Entered");

            if (_isGivingExp)
            {
                _logger.WriteLine("[GiveProtagExpHandler] Recursive call detected, calling original and returning");
                _giveProtagExpHook.OriginalFunction(results, param2, param3, param4);
                return;
            }

            _isGivingExp = true;

            try
            {
                int levelCap = GetCurrentLevelCap();
                int currentDay = _getTotalDay?.Invoke() ?? 0;
                _logger.WriteLine($"[GiveProtagExpHandler] Level cap: {levelCap} (Day: 0x{currentDay:X})");

                if (_getNumPersonas == null)
                {
                    _logger.WriteLine("[GiveProtagExpHandler] _getNumPersonas delegate is null, skipping persona checks");
                }
                else if (_getProtagPersona == null)
                {
                    _logger.WriteLine("[GiveProtagExpHandler] _getProtagPersona delegate is null, skipping persona checks");
                }
                else
                {
                    ushort numPersonas = _getNumPersonas();
                    _logger.WriteLine($"[GiveProtagExpHandler] Number of protagonist personas: {numPersonas}");

                    for (short slot = 0; slot < numPersonas; slot++)
                    {
                        IntPtr persona = _getProtagPersona(slot);
                        if (persona == IntPtr.Zero)
                        {
                            _logger.WriteLine($"[GiveProtagExpHandler] Persona at slot {slot} pointer is zero, skipping");
                            continue;
                        }
                        if (IsPersonaAtLevelCap(persona))
                        {
                            _logger.WriteLine($"[GiveProtagExpHandler] Protagonist persona {slot} at level cap, blocking exp");
                            ZeroExpGain(results, slot, true);
                        }
                        else
                        {
                            _logger.WriteLine($"[GiveProtagExpHandler] Protagonist persona {slot} below level cap");
                        }
                    }
                }

                _logger.WriteLine("[GiveProtagExpHandler] Calling original function");
                _giveProtagExpHook.OriginalFunction(results, param2, param3, param4);
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GiveProtagExpHandler] Exception: {ex.Message}");
                _giveProtagExpHook.OriginalFunction(results, param2, param3, param4);
            }
            finally
            {
                _isGivingExp = false;
                _logger.WriteLine("[GiveProtagExpHandler] Exiting");
            }
        }

        private void GivePartyMemberExpHandler(IntPtr results, IntPtr param2, IntPtr param3, IntPtr param4)
        {
            _logger.WriteLine("[GivePartyMemberExpHandler] Entered");

            if (_isGivingExp)
            {
                _logger.WriteLine("[GivePartyMemberExpHandler] Recursive call detected, calling original and returning");
                _givePartyMemberExpHook.OriginalFunction(results, param2, param3, param4);
                return;
            }

            _isGivingExp = true;

            try
            {
                int levelCap = GetCurrentLevelCap();
                int currentDay = _getTotalDay?.Invoke() ?? 0;
                _logger.WriteLine($"[GivePartyMemberExpHandler] Level cap: {levelCap} (Day: 0x{currentDay:X})");

                for (int i = 0; i < 4; i++)
                {
                    IntPtr partyMemberInfo = GetPartyMemberFromResults(results, i);
                    if (partyMemberInfo == IntPtr.Zero)
                    {
                        _logger.WriteLine($"[GivePartyMemberExpHandler] Party member {i} pointer is zero, skipping");
                        continue;
                    }
                    if (IsPartyMemberAtLevelCap(partyMemberInfo))
                    {
                        _logger.WriteLine($"[GivePartyMemberExpHandler] Party member {i} at level cap {levelCap}, blocking exp");
                        ZeroExpGain(results, i, false);
                    }
                    else
                    {
                        _logger.WriteLine($"[GivePartyMemberExpHandler] Party member {i} below level cap");
                    }
                }

                _logger.WriteLine("[GivePartyMemberExpHandler] Calling original function");
                _givePartyMemberExpHook.OriginalFunction(results, param2, param3, param4);
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GivePartyMemberExpHandler] Exception: {ex.Message}");
                _givePartyMemberExpHook.OriginalFunction(results, param2, param3, param4);
            }
            finally
            {
                _isGivingExp = false;
                _logger.WriteLine("[GivePartyMemberExpHandler] Exiting");
            }
        }

        private void GivePersonaExpHandler(IntPtr persona, uint exp)
        {
            _logger.WriteLine("[GivePersonaExpHandler] Entered");

            if (_isGivingExp)
            {
                _logger.WriteLine("[GivePersonaExpHandler] Recursive call detected, calling original and returning");
                _givePersonaExpHook.OriginalFunction(persona, exp);
                return;
            }

            try
            {
                if (persona == IntPtr.Zero)
                {
                    _logger.WriteLine("[GivePersonaExpHandler] Persona pointer is zero, skipping");
                    _givePersonaExpHook.OriginalFunction(persona, exp);
                    return;
                }

                if (IsPersonaAtLevelCap(persona))
                {
                    int currentDay = _getTotalDay?.Invoke() ?? 0;
                    _logger.WriteLine($"[GivePersonaExpHandler] Persona at level cap, setting exp to 0 (was {exp}) - Day: 0x{currentDay:X}");
                    _givePersonaExpHook.OriginalFunction(persona, 0);
                }
                else
                {
                    _logger.WriteLine($"[GivePersonaExpHandler] Persona below level cap, giving exp {exp}");
                    _givePersonaExpHook.OriginalFunction(persona, exp);
                }
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GivePersonaExpHandler] Exception: {ex.Message}");
                _givePersonaExpHook.OriginalFunction(persona, exp);
            }

            _logger.WriteLine("[GivePersonaExpHandler] Exiting");
        }

        private void ZeroExpGain(IntPtr results, int index, bool isProtag)
        {
            _logger.WriteLine($"[ZeroExpGain] Entered (Index: {index}, IsProtag: {isProtag})");

            try
            {
                if (isProtag)
                {
                    IntPtr expGainsPtr = results + 0x200; // Verify offset
                    _logger.WriteLine($"[ZeroExpGain] Zeroing protagonist exp gain at 0x{expGainsPtr + (index * 4):X}");
                    Marshal.WriteInt32(expGainsPtr + (index * 4), 0);
                }
                else
                {
                    IntPtr expGainsPtr = results + 0x100; // Verify offset
                    _logger.WriteLine($"[ZeroExpGain] Zeroing party member exp gain at 0x{expGainsPtr + (index * 4):X}");
                    Marshal.WriteInt32(expGainsPtr + (index * 4), 0);
                }
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[ZeroExpGain] Exception: {ex.Message}");
            }

            _logger.WriteLine("[ZeroExpGain] Exiting");
        }

        private IntPtr GetPartyMemberFromResults(IntPtr results, int index)
        {
            _logger.WriteLine($"[GetPartyMemberFromResults] Entered (Index: {index})");

            try
            {
                IntPtr partyMembersPtr = results + 0x80; // Verify offset
                IntPtr memberPtr = Marshal.ReadIntPtr(partyMembersPtr + (index * IntPtr.Size));
                _logger.WriteLine($"[GetPartyMemberFromResults] Party member {index} pointer: 0x{memberPtr:X}");
                return memberPtr;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[GetPartyMemberFromResults] Exception: {ex.Message}");
                return IntPtr.Zero;
            }
        }


        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            _configuration = configuration;
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}