#pragma once

#include <cstdint>
#include <string>
#include <vector>

// Minimal 16-bit PCM WAV helpers (replaces delta-studio's ysWindowsAudioWaveFile).
bool readWav16Mono(const std::string &path, std::vector<int16_t> *samples, int *sampleRate);
bool writeWav16Mono(const std::string &path, const std::vector<int16_t> &samples, int sampleRate);
