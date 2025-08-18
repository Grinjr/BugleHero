using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BugleHero.Patches;
using HarmonyLib;
using System;
using System.Linq;

namespace BugleHero
{
	[BepInPlugin(modGUID, modName, modVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string modGUID = "Grin.BugleHero";
		public const string modName = "Bugle Hero";
		public const string modVersion = "0.2.0";
		private readonly Harmony harmony = new Harmony(modGUID);
		internal static Plugin Instance;
		internal ManualLogSource mls;
		private MidiInputHandler midiHandler;

		// Config entries
		public ConfigEntry<string> MidiDeviceName { get; private set; }

		void Awake()
		{
			if (Instance == null)
			{
				Instance = this;
			}
			mls = Logger;
			mls.LogInfo("Warming up the brass...");
			InitConfig();
			mls.LogInfo($"Tootware version {modVersion} initialized.");

			mls.LogInfo("Calibrating toot pressure...");
			midiHandler = new MidiInputHandler();
			midiHandler.Initialize(Config); // pass the plugin config directly
			harmony.PatchAll();
			mls.LogInfo("Toot toot! Ready for duty.");

			mls.LogInfo("Ready to bugle at a moment's tootice.");
			mls.LogInfo("Practice safe tooting.");
		}

		void Update()
		{
			midiHandler?.ProcessMainThreadQueue();
		}

		void InitConfig()
		{
			// Collect device names
			var deviceNames = Enumerable.Range(0, NAudio.Midi.MidiIn.NumberOfDevices)
										.Select(i => NAudio.Midi.MidiIn.DeviceInfo(i).ProductName)
										.ToArray();

			if (deviceNames.Length == 0)
			{
				mls.LogWarning("No MIDI devices found.");
				deviceNames = new string[] { "No MIDI devices detected" };
			}

			MidiDeviceName = Config.Bind(
				"MIDI",
				"DeviceName",
				deviceNames[0],
				new ConfigDescription(
					"Select MIDI input device",
					null,
					new AcceptableValueList<string>(deviceNames)
				)
			);

			// Forward setting changes directly to the handler
			MidiDeviceName.SettingChanged += (s, e) =>
			{
				midiHandler?.OpenMidiDeviceByName(MidiDeviceName.Value);
			};
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