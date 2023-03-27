using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using System.Timers;

namespace HeelsPlugin
{
  public class PluginMemory
  {
    public IntPtr playerMovementFunc;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate void PlayerMovementDelegate(IntPtr player);
    public readonly Hook<PlayerMovementDelegate> playerMovementHook;
    public Dictionary<GameObject, float> PlayerOffsets = new();

    private Timer timer;
    private float? lastOffset = null;

    private GameObject PlayerSelf => Plugin.ObjectTable.First();

    public PluginMemory()
    {
      playerMovementFunc = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B 03 48 8B CB FF 50 ?? 83 F8 ?? 75 ??");
      playerMovementHook = Hook<PlayerMovementDelegate>.FromAddress(playerMovementFunc, new PlayerMovementDelegate(PlayerMovementHook));

	  timer = new Timer(1000);
	  timer.Elapsed += (source, e) => correctOffsetOnAnimation(Plugin.Configuration.customSit);
	  timer.AutoReset = true;


      playerMovementHook.Enable();
    }

    public void Dispose()
    {
      try
      {
        if (playerMovementHook != null)
        {
          playerMovementHook?.Disable();
          playerMovementHook?.Dispose();
        }
        timer.Stop();
        timer.Dispose();
      }
      catch
      {
      }
    }

    private ConfigModel? GetConfig(EquipItem inModel)
    {
      var foundConfig = Plugin.Configuration?.Configs.Where(config =>
      {
        var valid = false;
        if (config.ModelMain > 0)
        {
          var configModel = new EquipItem(config.ModelMain);
          valid = configModel.Main == inModel.Main && configModel.Variant == inModel.Variant;
        }
        else
          valid = config.Model == inModel.Main;

        return valid && config.Enabled;
      });

      // return the last one in the list
      if (foundConfig != null && foundConfig.Any())
        return foundConfig.Last();

      return null;
    }

    private ConfigModel? GetConfig(IntPtr addr)
    {
      var feet = GetPlayerFeet(addr);
      if (!feet.HasValue)
        return null;
      return GetConfig(feet.Value);
    }

    public void RestorePlayerY()
    {
      var player = PlayerSelf;
      if (player != null)
      {
        SetPosition(player.Position.Y, player.Address, true);
        PlayerMove(player.Address);
      }
    }

    public EquipItem? GetPlayerFeetItem()
    {
      var player = PlayerSelf.Address;
      return GetPlayerFeet(player);
    }

    public EquipItem? GetPlayerFeet(IntPtr? player)
    {
      if (!player.HasValue)
        return null;

      var feet = (uint)Marshal.ReadInt32(player.Value + 0x830 + 0x10);
      return new EquipItem(feet);
    }

    private unsafe bool IsConfigValidForActor(IntPtr player, ConfigModel? config)
    {
      if (config is { SelfOnly: true } && player != PlayerSelf.Address) return false;
      // create game object from pointer
      var character = (SCharacter*)player;

      if (character == null) return false;

      var customize = character->DrawData.CustomizeData;

      // get the race and sex of character for filtering on config
      var race = (Races)(1 << customize[(int)CustomizeIndex.Race] - 1);
      var sex = (Sexes)customize[(int)CustomizeIndex.Gender] + 1;

      var containsRace = (config?.RaceFilter & race) == race;
      var containsSex = (config?.SexFilter & sex) == sex;

      if (character->Mode == SCharacter.CharacterModes.InPositionLoop && character->ModeParam is 1 or 2 or 3) return false;
      if (character->Mode == SCharacter.CharacterModes.EmoteLoop && character->ModeParam is 21) return false;

      if (config != null && config.Enabled && containsRace && containsSex)
        return true;

      return false;
    }

    public float GetPlayerOffset()
    {
      var feet = GetPlayerFeetItem();
      if (!feet.HasValue)
        return 0;
      var config = GetConfig(feet.Value);

      return config?.Offset ?? 0;
    }

    public unsafe void PlayerMove(IntPtr player)
    {
      try
      {
        if (player == PlayerSelf.Address)
        {
          ProcessSelf();
          goto processPlayer;
        }
        else
        {
          var playerObject = Plugin.ObjectTable.CreateObjectReference(player);

          // check against dictionary created from IPC
          if (playerObject != null && PlayerOffsets.ContainsKey(playerObject))
          {
            SetPosition(PlayerOffsets[playerObject], player);
          }
          else
          {
            goto processPlayer;
          }
        }
        return;

      processPlayer:
        {
          var config = GetConfig(player);
          if (config != null && IsConfigValidForActor(player, config))
            SetPosition(config.Offset, player);
        }
      }
      catch
      {
      }
    }

    private void ProcessSelf()
    {
      var config = GetConfig(PlayerSelf.Address);
      if (lastOffset != config?.Offset && config?.Offset != null)
        Plugin.Ipc?.OnOffsetChange(config.Offset);
      lastOffset = config?.Offset;
    }

    private unsafe void PlayerMovementHook(IntPtr player)
    {
      // Call the original function.
      playerMovementHook.Original(player);
      timer.Enabled = true;
      PlayerMove(player);
    }

    private (IntPtr, Vector3) GetPosition(IntPtr actor)
    {
      try
      {
        var modelPtr = Marshal.ReadInt64(actor, 0x100);
        if (modelPtr == 0)
          return (IntPtr.Zero, Vector3.Zero);
        var positionPtr = new IntPtr(modelPtr + 0x50);
        return (positionPtr, Marshal.PtrToStructure<Vector3>(positionPtr));
      }
      catch
      {
        return (IntPtr.Zero, Vector3.Zero);
      }
    }

    public void SetPosition(float offset, IntPtr actor, bool replace = false)
    {
      try
      {
        if (actor != IntPtr.Zero)
        {
          var (positionPtr, position) = GetPosition(actor);
          if (positionPtr == IntPtr.Zero)
            return;

          // Offset the Y coordinate.
          if (replace)
            position.Y = offset;
          else
            position.Y += offset;
          PluginLog.Verbose("Offsetting " + actor + " by: " + offset);

          Marshal.StructureToPtr(position, positionPtr, false);
        }
      }
      catch(Exception e) {
        PluginLog.Error("Encountered an error during setting the Position: ", e);
      }
    }

    // Reset position of your char if you are Sitting/Dozing etc.
    public void correctOffsetOnAnimation(float offset) {
      int animID = GetAnimation(PlayerSelf.Address);

      if (validAnimIds.Contains(animID) && Plugin.Configuration.disableSit) {
        if (Plugin.Configuration.customSitEnable) {
			PluginLog.Debug("Found Animation to use Custom Offset: " + animID);
			timer.Enabled = false;
			SetPosition(PlayerSelf.Position.Y + offset, PlayerSelf.Address, true);
        } else {
			PluginLog.Debug("Found Animation to not trigger: " + animID);
			timer.Enabled = false;
			SetPosition(PlayerSelf.Position.Y, PlayerSelf.Address, true);
        }
	  }
    }

    public unsafe int GetAnimation(IntPtr player) {
      var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player;
      return chara->ActionTimelineManager.Driver.TimelineIds[0];
    }

       //     animID == 584 || Doze Start 1
       //     animID == 585 || Doze Loop 1
       //     animID == 653 || Ground Sit Start 1
       //     animID == 654 || Ground Sit Loop 1
       //     animID == 3770 || Ground Sit Start 4
       //     animID == 3771 || Ground Sit Loop 4
       //     animID == 642 || Sit Start 1
       //     animID == 643 || Sit Loop 1
       //     animID >= 3131 && animID <= 3142 || Contains Start and Loop of Doze 2 + 3; Ground Sit 2 + 3; Sit 2 + 3
       //     animID >= 8001 && animID <= 8004    Contains Start and Loop of Sit 4 + 5
    HashSet<int> validAnimIds = new HashSet<int> {
      584, 585, 653, 654, 3770, 3771, 642,
      643, 3131, 3132, 3133, 3134, 3135, 3136, 3137, 3138, 3139, 3140, 3141, 3142,
      8001, 8002, 8003, 8004
    };
  }
}