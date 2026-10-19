// spsc_ring.h: lock-free single-producer single-consumer rings (PLAN 3.4, 3.6; research 01 4.4).
//
// SpscQueue<T, N>: fixed-capacity queue of trivially copyable items (worker commands).
// SpscFloatRing:   power-of-two float ring for audio. The producer (engine worker) never touches the read index and
//                  drops the newest samples when the ring is full; the consumer (audio thread) never allocates.
// Indices are 64-bit and never wrap in practice; each sits on its own cache line.
#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>
#include <vector>

namespace esp {

constexpr size_t kCacheLine = 64;

template <class T, size_t N>
class SpscQueue {
    static_assert((N & (N - 1)) == 0, "capacity must be a power of two");
    static_assert(std::is_trivially_copyable<T>::value, "items are copied with memcpy semantics");

public:
    // Producer.
    bool push(const T &item) {
        const uint64_t w = m_write.load(std::memory_order_relaxed);
        if (w - m_read.load(std::memory_order_acquire) >= N) return false;
        m_items[w & (N - 1)] = item;
        m_write.store(w + 1, std::memory_order_release);
        return true;
    }
    // Consumer.
    bool pop(T &item) {
        const uint64_t r = m_read.load(std::memory_order_relaxed);
        if (r == m_write.load(std::memory_order_acquire)) return false;
        item = m_items[r & (N - 1)];
        m_read.store(r + 1, std::memory_order_release);
        return true;
    }
    size_t size() const {
        const uint64_t w = m_write.load(std::memory_order_acquire);
        const uint64_t r = m_read.load(std::memory_order_acquire);
        return static_cast<size_t>(w - r);
    }
    static constexpr size_t capacity() { return N; }

private:
    alignas(kCacheLine) std::atomic<uint64_t> m_write{0};
    alignas(kCacheLine) std::atomic<uint64_t> m_read{0};
    alignas(kCacheLine) T m_items[N];
};

class SpscFloatRing {
public:
    void init(size_t capacityPow2) {
        m_buf.assign(capacityPow2, 0.0f);
        m_mask = capacityPow2 - 1;
        m_write.store(0);
        m_read.store(0);
    }
    size_t capacity() const { return m_buf.size(); }

    // Producer: writes what fits, returns the number written (the rest is dropped by the caller's accounting).
    size_t push(const float *src, size_t n) {
        const uint64_t w = m_write.load(std::memory_order_relaxed);
        const uint64_t r = m_read.load(std::memory_order_acquire);
        const size_t space = m_buf.size() - static_cast<size_t>(w - r);
        if (n > space) n = space;
        const size_t at = static_cast<size_t>(w & m_mask);
        const size_t first = n < m_buf.size() - at ? n : m_buf.size() - at;
        std::memcpy(&m_buf[at], src, first * sizeof(float));
        if (n > first) std::memcpy(&m_buf[0], src + first, (n - first) * sizeof(float));
        m_write.store(w + n, std::memory_order_release);
        return n;
    }
    // Consumer: reads up to n, returns the number read.
    size_t pop(float *dst, size_t n) {
        const uint64_t r = m_read.load(std::memory_order_relaxed);
        const uint64_t w = m_write.load(std::memory_order_acquire);
        const size_t avail = static_cast<size_t>(w - r);
        if (n > avail) n = avail;
        const size_t at = static_cast<size_t>(r & m_mask);
        const size_t first = n < m_buf.size() - at ? n : m_buf.size() - at;
        std::memcpy(dst, &m_buf[at], first * sizeof(float));
        if (n > first) std::memcpy(dst + first, &m_buf[0], (n - first) * sizeof(float));
        m_read.store(r + n, std::memory_order_release);
        return n;
    }
    // Either side: frames currently buffered.
    size_t fill() const {
        const uint64_t w = m_write.load(std::memory_order_acquire);
        const uint64_t r = m_read.load(std::memory_order_acquire);
        return static_cast<size_t>(w - r);
    }

private:
    std::vector<float> m_buf;
    size_t m_mask = 0;
    alignas(kCacheLine) std::atomic<uint64_t> m_write{0};
    alignas(kCacheLine) std::atomic<uint64_t> m_read{0};
};

} // namespace esp
