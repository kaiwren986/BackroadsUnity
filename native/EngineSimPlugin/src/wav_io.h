// wav_io.h: our own 16-bit PCM WAV reader (impulse responses, replacing delta-studio's ysWindowsAudioWaveFile) and a
// writer for taps and test renders (research 01 4.2, PLAN 1.6).
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace esp {

struct WavData {
    int sampleRate = 0;
    int channels = 0;
    std::vector<int16_t> samples;  // interleaved
    size_t frames() const { return channels > 0 ? samples.size() / static_cast<size_t>(channels) : 0; }
};

// RIFF/WAVE, PCM (format 1 or WAVE_FORMAT_EXTENSIBLE with PCM), 16 bits. Paths are UTF-8.
bool readWav16(const std::string &pathUtf8, WavData &out, std::string &err);
// First channel as int16.
std::vector<int16_t> firstChannel(const WavData &w);
// Mono floats in [-1, 1] written as 16-bit PCM (clamped).
bool writeWav16Mono(const std::string &pathUtf8, const float *samples, size_t n, int sampleRate, std::string &err);

// UTF-8 -> UTF-16 for Windows file APIs.
std::wstring widen(const std::string &utf8);

} // namespace esp
