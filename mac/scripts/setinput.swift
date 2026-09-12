// Lists input devices and switches the system default input, so the
// AVAudioEngineConfigurationChange path can be exercised for real instead of by hand.
//   swift setinput.swift            -> list
//   swift setinput.swift <deviceID> -> set default input
import CoreAudio
import Foundation

func devices() -> [AudioDeviceID] {
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size)
    var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &ids)
    return ids
}

func name(_ id: AudioDeviceID) -> String {
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioObjectPropertyName,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    var out: CFString = "" as CFString
    var size = UInt32(MemoryLayout<CFString>.size)
    AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &out)
    return out as String
}

func inputChannels(_ id: AudioDeviceID) -> Int {
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioDevicePropertyStreamConfiguration,
        mScope: kAudioDevicePropertyScopeInput,
        mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size)
    guard size > 0 else { return 0 }
    let buf = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: 16)
    defer { buf.deallocate() }
    AudioObjectGetPropertyData(id, &addr, 0, nil, &size, buf)
    let lists = buf.assumingMemoryBound(to: AudioBufferList.self)
    return UnsafeMutableAudioBufferListPointer(lists).reduce(0) { $0 + Int($1.mNumberChannels) }
}

func defaultInput() -> AudioDeviceID {
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDefaultInputDevice,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    var id: AudioDeviceID = 0
    var size = UInt32(MemoryLayout<AudioDeviceID>.size)
    AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &id)
    return id
}

func setDefaultInput(_ id: AudioDeviceID) -> Bool {
    var addr = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDefaultInputDevice,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    var value = id
    let status = AudioObjectSetPropertyData(
        AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil,
        UInt32(MemoryLayout<AudioDeviceID>.size), &value)
    return status == noErr
}

let args = CommandLine.arguments
if args.count > 1, let wanted = AudioDeviceID(args[1]) {
    print(setDefaultInput(wanted) ? "default input -> \(name(wanted)) [\(wanted)]" : "failed to set \(wanted)")
} else {
    let cur = defaultInput()
    for id in devices() where inputChannels(id) > 0 {
        print("\(id == cur ? "*" : " ") \(id)\t\(name(id))\t\(inputChannels(id))ch")
    }
}
