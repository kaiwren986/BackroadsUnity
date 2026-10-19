// builders.cpp: the builtin table and bundle teardown.
#include "builders.h"

#include "engine.h"
#include "transmission.h"
#include "vehicle.h"

namespace esp {

namespace {

struct Entry {
    const char *name;
    bool (*fn)(const std::string &, EngineBundle &);
};

const Entry kBuiltins[] = {
    {"f4_boxer_a", buildF4BoxerA},
    {"v8_cross_a", buildV8CrossA},
    {"i4_synth", buildI4Synth},
};

} // namespace

const char *builtinNameList() { return "f4_boxer_a,v8_cross_a,i4_synth"; }

bool buildBuiltin(const std::string &name, const std::string &esDir, EngineBundle &out, std::string &err) {
    for (const Entry &e : kBuiltins) {
        if (name == e.name) {
            if (!e.fn(esDir, out)) {
                err = "builder failed: " + name;
                return false;
            }
            return true;
        }
    }
    err = "unknown builtin '" + name + "' (have: " + builtinNameList() + ")";
    return false;
}

void destroyBundle(EngineBundle &b) {
    if (b.engine != nullptr) {
        b.engine->destroy();
        delete b.engine;
    }
    delete b.vehicle;
    delete b.transmission;
    b = EngineBundle();
}

} // namespace esp
