// piranha_path_std.cpp: drop-in replacement for piranha/src/path.cpp on C++17 std::filesystem instead of
// Boost.Filesystem (research 01 5.4, App. A.6; probes/p01/piranha_path_std.cpp). piranha/include/path.h only
// forward-declares boost::filesystem::path, so that name is defined here as a thin subclass of std::filesystem::path;
// no piranha file is modified. Built into the piranha target in place of path.cpp.
#include <filesystem>
#include <system_error>

namespace boost {
namespace filesystem {
class path : public std::filesystem::path {
public:
    using std::filesystem::path::path;
    path() = default;
    path(const std::filesystem::path &p) : std::filesystem::path(p) {}
};
} // namespace filesystem
} // namespace boost

#include "piranha/include/memory_tracker.h"
#include "piranha/include/path.h"

namespace fs = std::filesystem;

piranha::Path::Path(const std::string &path) {
    m_path = nullptr;
    setPath(path);
}

piranha::Path::Path(const char *path) {
    m_path = nullptr;
    setPath(std::string(path));
}

piranha::Path::Path(const Path &path) {
    m_path = TRACK(new boost::filesystem::path);
    *m_path = path.getBoostPath();
}

piranha::Path::Path() { m_path = nullptr; }

piranha::Path::Path(const boost::filesystem::path &path) {
    m_path = TRACK(new boost::filesystem::path);
    *m_path = path;
}

piranha::Path::~Path() {
    if (m_path != nullptr) delete FTRACK(m_path);
}

std::string piranha::Path::toString() const { return m_path->string(); }

void piranha::Path::setPath(const std::string &path) {
    if (m_path != nullptr) delete FTRACK(m_path);
    m_path = TRACK(new boost::filesystem::path(path));
}

bool piranha::Path::operator==(const Path &path) const {
    // boost::filesystem::equivalent throws when neither file exists; fall back to a lexical comparison instead
    std::error_code ec;
    const bool eq = fs::equivalent(getBoostPath(), path.getBoostPath(), ec);
    return ec ? (getBoostPath().lexically_normal() == path.getBoostPath().lexically_normal()) : eq;
}

piranha::Path piranha::Path::append(const Path &path) const {
    return Path(boost::filesystem::path(fs::path(getBoostPath()) / fs::path(path.getBoostPath())));
}

void piranha::Path::getParentPath(Path *path) const {
    if (path->m_path != nullptr) delete FTRACK(path->m_path);  // upstream leaks the previous value here
    path->m_path = TRACK(new boost::filesystem::path);
    *path->m_path = boost::filesystem::path(m_path->parent_path());
}

const piranha::Path &piranha::Path::operator=(const Path &b) {
    if (this == &b) return *this;
    if (m_path != nullptr) delete FTRACK(m_path);
    m_path = TRACK(new boost::filesystem::path);
    *m_path = b.getBoostPath();
    return *this;
}

std::string piranha::Path::getExtension() const { return m_path->extension().string(); }

std::string piranha::Path::getStem() const { return m_path->stem().string(); }

bool piranha::Path::isAbsolute() const { return m_path->is_absolute(); }  // boost's is_complete()

bool piranha::Path::exists() const {
    std::error_code ec;  // never throw from a path check
    return fs::exists(*m_path, ec);
}
