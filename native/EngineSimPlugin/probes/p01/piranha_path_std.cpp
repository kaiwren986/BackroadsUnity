// Drop-in replacement for piranha/src/path.cpp using C++17 std::filesystem instead of Boost.Filesystem.
// piranha/include/path.h only forward-declares boost::filesystem::path, so we define that name here
// as a thin subclass of std::filesystem::path. No piranha header is modified.
#include <filesystem>
namespace boost { namespace filesystem {
    class path : public std::filesystem::path {
    public:
        using std::filesystem::path::path;
        path() = default;
        path(const std::filesystem::path &p) : std::filesystem::path(p) {}
    };
} }

#include "piranha/include/path.h"
#include "piranha/include/memory_tracker.h"

namespace fs = std::filesystem;

piranha::Path::Path(const std::string &path) { m_path = nullptr; setPath(path); }
piranha::Path::Path(const char *path) { m_path = nullptr; setPath(std::string(path)); }
piranha::Path::Path(const Path &path) { m_path = TRACK(new boost::filesystem::path); *m_path = path.getBoostPath(); }
piranha::Path::Path() { m_path = nullptr; }
piranha::Path::Path(const boost::filesystem::path &path) { m_path = TRACK(new boost::filesystem::path); *m_path = path; }
piranha::Path::~Path() { if (m_path != nullptr) delete FTRACK(m_path); }
std::string piranha::Path::toString() const { return m_path->string(); }
void piranha::Path::setPath(const std::string &path) {
    if (m_path != nullptr) delete FTRACK(m_path);
    m_path = TRACK(new boost::filesystem::path(path));
}
bool piranha::Path::operator==(const Path &path) const {
    std::error_code ec;
    const bool eq = fs::equivalent(getBoostPath(), path.getBoostPath(), ec);
    return ec ? (getBoostPath().lexically_normal() == path.getBoostPath().lexically_normal()) : eq;
}
piranha::Path piranha::Path::append(const Path &path) const {
    return Path(boost::filesystem::path(fs::path(getBoostPath()) / fs::path(path.getBoostPath())));
}
void piranha::Path::getParentPath(Path *path) const {
    path->m_path = TRACK(new boost::filesystem::path);
    *path->m_path = boost::filesystem::path(m_path->parent_path());
}
const piranha::Path &piranha::Path::operator=(const Path &b) {
    if (m_path != nullptr) delete FTRACK(m_path);
    m_path = TRACK(new boost::filesystem::path);
    *m_path = b.getBoostPath();
    return *this;
}
std::string piranha::Path::getExtension() const { return m_path->extension().string(); }
std::string piranha::Path::getStem() const { return m_path->stem().string(); }
bool piranha::Path::isAbsolute() const { return m_path->is_absolute(); }
bool piranha::Path::exists() const { return fs::exists(*m_path); }
