using BugleHero;
using BugleHero.Patches;
using NAudio.Midi;
using System.Linq;
using BepInEx.Configuration;
using System.Collections.Concurrent;
using UnityEngine;
using System;

public class MidiInputHandler
{
	private MidiIn midiIn;
	private ConfigEntry<string> midiDeviceName;

	private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();

	private void EnqueueOnMainThread(Action a)
	{
		if (a != null) mainThreadQueue.Enqueue(a);
	}

	public void ProcessMainThreadQueue()
	{
		while (mainThreadQueue.TryDequeue(out var action))
		{
			try { action(); }
			catch (Exception ex)
			{
				Plugin.Instance.mls.LogWarning("Exception processing MIDI action: " + ex);
			}
		}
	}

	public void Initialize(ConfigFile config)
	{
		var deviceNamesList = Enumerable.Range(0, MidiIn.NumberOfDevices)
										.Select(i => MidiIn.DeviceInfo(i).ProductName)
										.ToList();

		if (deviceNamesList.Count == 0)
		{
			Plugin.Instance.mls.LogInfo("No MIDI devices detected.");
			return;
		}

		midiDeviceName = config.Bind(
			"MIDI",
			"DeviceName",
			deviceNamesList.First(),
			new ConfigDescription("Select MIDI input device", null,
				new AcceptableValueList<string>(deviceNamesList.ToArray()))
		);

		midiDeviceName.SettingChanged += (s, e) =>
		{
			OpenMidiDeviceByName(midiDeviceName.Value);
		};

		OpenMidiDeviceByName(midiDeviceName.Value);
	}

	public void OpenMidiDeviceByName(string deviceName)
	{
		int deviceIndex = -1;
		for (int i = 0; i < MidiIn.NumberOfDevices; i++)
		{
			if (MidiIn.DeviceInfo(i).ProductName == deviceName)
			{
				deviceIndex = i;
				break;
			}
		}

		if (deviceIndex < 0)
		{
			Plugin.Instance.mls.LogWarning($"MIDI device '{deviceName}' not found.");
			return;
		}

		StopListening();

		midiIn = new MidiIn(deviceIndex);
		midiIn.MessageReceived += MidiIn_MessageReceived;
		midiIn.ErrorReceived += MidiIn_ErrorReceived;
		midiIn.Start();

		Plugin.Instance.mls.LogInfo($"Started listening to MIDI device #{deviceIndex}: {deviceName}");
	}

	private void MidiIn_MessageReceived(object sender, MidiInMessageEventArgs e)
	{
		try
		{
			if (e.MidiEvent is NoteOnEvent noteOn)
			{
				int midiNote = noteOn.NoteNumber;

				if (noteOn.Velocity > 0)
				{
					EnqueueOnMainThread(() =>
					{
						var bugleInstance = FindLocalBugleSFX();
						if (bugleInstance != null)
						{
							BugleMIDIPatch.OnMidiNoteOn(midiNote, bugleInstance);
						}
					});
				}
				else
				{
					EnqueueOnMainThread(() =>
					{
						var bugleInstance = FindLocalBugleSFX();
						if (bugleInstance != null)
						{
							BugleMIDIPatch.OnMidiNoteOff(midiNote, bugleInstance);
						}
					});
				}
			}
			else if (e.MidiEvent is NoteEvent noteEvent && noteEvent.CommandCode == MidiCommandCode.NoteOff)
			{
				EnqueueOnMainThread(() =>
				{
					var bugleInstance = FindLocalBugleSFX();
					if (bugleInstance != null)
					{
						BugleMIDIPatch.OnMidiNoteOff(noteEvent.NoteNumber, bugleInstance);
					}
				});
			}
		}
		catch (Exception ex)
		{
			Plugin.Instance.mls.LogWarning("Exception in MIDI handler: " + ex);
		}
	}

	private void MidiIn_ErrorReceived(object sender, MidiInMessageEventArgs e)
	{
		Plugin.Instance.mls.LogWarning("MIDI error: " + e.RawMessage);
	}

	public void StopListening()
	{
		if (midiIn != null)
		{
			midiIn.Stop();
			midiIn.Dispose();
			midiIn = null;
		}
	}

	private BugleSFX FindLocalBugleSFX()
	{
		return UnityEngine.Object.FindObjectsOfType<BugleSFX>()
			.FirstOrDefault(b => b.photonView.IsMine);
	}
}
