using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BugleHero.Patches;
using HarmonyLib;
using System;

namespace BugleHero
{
	[BepInPlugin(modGUID, modName, modVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string modGUID = "Grin.BugleHero";
		public const string modName = "Bugle Hero";
		public const string modVersion = "0.0.1.0";

		private readonly Harmony harmony = new Harmony(modGUID);

		internal static Plugin Instance;

		internal ManualLogSource mls;

		private MidiInputHandler midiHandler;

		// Config entry for MIDI device name
		public ConfigEntry<string> MidiDeviceName { get; private set; }

		void Awake()
		{
			if (Instance == null)
			{
				Instance = this;
			}

			mls = Logger;

			mls.LogInfo("Warming up the brass...");
			mls.LogInfo("Toot toot! Ready for duty.");
			mls.LogInfo("Calibrating toot pressure...");
			mls.LogInfo("Now 400% more toot.");
			mls.LogInfo($"Tootware version {modVersion} initialized.");
			mls.LogInfo("Ready to bugle at a moment’s tootice.");
			mls.LogInfo("Practice safe tooting.");

			InitConfig();
			InitMidi();

			harmony.PatchAll();
		}

		void Update()
		{
			midiHandler?.ProcessMainThreadQueue();
		}

		void InitConfig()
		{
			// Collect device names
			var deviceNames = new System.Collections.Generic.List<string>();
			for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++)
			{
				deviceNames.Add(NAudio.Midi.MidiIn.DeviceInfo(i).ProductName);
			}

			if (deviceNames.Count == 0)
			{
				mls.LogWarning("No MIDI devices found for config.");
				deviceNames.Add("No MIDI devices detected");
			}

			// Bind config with AcceptableValueList
			string[] deviceNamesArray = deviceNames.ToArray();

			MidiDeviceName = Config.Bind(
				"MIDI",
				"DeviceName",
				deviceNamesArray[0],
				new ConfigDescription(
					"Select MIDI input device",
					null,
					new AcceptableValueList<string>(deviceNamesArray)
				)
			);
		}

		void InitMidi()
		{
			midiHandler = new MidiInputHandler();

			// Listen to config changes to switch MIDI device dynamically
			MidiDeviceName.SettingChanged += (sender, args) =>
			{
				midiHandler.OpenMidiDeviceByName(MidiDeviceName.Value);
			};

			midiHandler.OpenMidiDeviceByName(MidiDeviceName.Value);
		}

		public void DisposeMidi()
		{
			midiHandler?.StopListening();
		}

		void OnDestroy()
		{
			try { DisposeMidi(); } catch { }
			try { harmony.UnpatchSelf(); } catch { }
		}

	}
}
