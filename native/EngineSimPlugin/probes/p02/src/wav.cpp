#include "wav.h"

#include <cstdio>
#include <cstring>

namespace {
    uint32_t rd32(const unsigned char *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24); }
    uint16_t rd16(const unsigned char *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
}

bool readWav16Mono(const std::string &path, std::vector<int16_t> *samples, int *sampleRate) {
    FILE *f = std::fopen(path.c_str(), "rb");
    if (f == nullptr) return false;
    std::vector<unsigned char> data;
    unsigned char buf[65536];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) data.insert(data.end(), buf, buf + n);
    std::fclose(f);

    if (data.size() < 12 || std::memcmp(data.data(), "RIFF", 4) != 0 || std::memcmp(data.data() + 8, "WAVE", 4) != 0) {
        return false;
    }

    int channels = 0, bits = 0;
    size_t pos = 12;
    while (pos + 8 <= data.size()) {
        const unsigned char *chunk = data.data() + pos;
        const uint32_t size = rd32(chunk + 4);
        if (std::memcmp(chunk, "fmt ", 4) == 0) {
            channels = rd16(chunk + 10);
            *sampleRate = (int)rd32(chunk + 12);
            bits = rd16(chunk + 22);
        }
        else if (std::memcmp(chunk, "data", 4) == 0) {
            if (bits != 16 || channels < 1) return false;
            const size_t frames = size / (2 * channels);
            samples->resize(frames);
            for (size_t i = 0; i < frames; ++i) {
                (*samples)[i] = (int16_t)rd16(chunk + 8 + i * 2 * channels); // first channel only
            }
            return true;
        }
        pos += 8 + size + (size & 1);
    }

    return false;
}

bool writeWav16Mono(const std::string &path, const std::vector<int16_t> &samples, int sampleRate) {
    FILE *f = std::fopen(path.c_str(), "wb");
    if (f == nullptr) return false;

    const uint32_t dataBytes = (uint32_t)(samples.size() * 2);
    auto w32 = [&](uint32_t v) { unsigned char b[4] = { (unsigned char)v, (unsigned char)(v >> 8), (unsigned char)(v >> 16), (unsigned char)(v >> 24) }; std::fwrite(b, 1, 4, f); };
    auto w16 = [&](uint16_t v) { unsigned char b[2] = { (unsigned char)v, (unsigned char)(v >> 8) }; std::fwrite(b, 1, 2, f); };

    std::fwrite("RIFF", 1, 4, f); w32(36 + dataBytes); std::fwrite("WAVE", 1, 4, f);
    std::fwrite("fmt ", 1, 4, f); w32(16); w16(1); w16(1); w32((uint32_t)sampleRate); w32((uint32_t)sampleRate * 2); w16(2); w16(16);
    std::fwrite("data", 1, 4, f); w32(dataBytes);
    std::fwrite(samples.data(), 2, samples.size(), f);
    std::fclose(f);
    return true;
}
