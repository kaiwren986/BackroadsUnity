// script_loader.cpp: see script_loader.h. Built only with ESP_WITH_PIRANHA.
#include "script_loader.h"

#include "EngineSimPlugin.h"
#include "esp_log.h"

#include "scripting/include/compiler.h"  // es_script::Compiler (output() static), LanguageRules, piranha

#include <windows.h>

#include <atomic>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>
#include <system_error>

namespace esp {

namespace {

std::mutex g_compileLock;  // es_script::Compiler::output() is one process-wide static
std::atomic<uint32_t> g_wrapperCounter{0};

namespace fs = std::filesystem;

std::string forwardSlashes(std::string s) {
    for (char &c : s)
        if (c == '\\') c = '/';
    return s;
}

std::string describe(const piranha::CompilationError *e) {
    std::ostringstream o;
    const piranha::ErrorCode_struct &code = e->getErrorCode();
    o << e->getCompilationUnit()->getPath().getStem() << "(" << e->getErrorLocation()->lineStart << "): error "
      << code.stage << code.code << ": " << code.info;
    for (piranha::IrContextTree *c = e->getInstantiation(); c != nullptr; c = c->getParent()) {
        piranha::IrNode *instance = c->getContext();
        if (instance == nullptr) continue;
        const std::string def = instance->getDefinition() != nullptr ? instance->getDefinition()->getName() : "<type error>";
        o << " | while instantiating " << instance->getParentUnit()->getPath().getStem() << "("
          << instance->getSummaryToken()->lineStart << "): " << (instance->getName().empty() ? "<unnamed>" : instance->getName())
          << " " << def;
    }
    return o.str();
}

} // namespace

int32_t compileEngineScript(const std::string &mrPath, const std::string &topNode, const std::string &esDir,
                            const std::string &tempDir, EngineBundle &out, std::string &err) {
    std::error_code ec;
    const fs::path engine = fs::absolute(fs::u8path(mrPath), ec);
    if (ec || !fs::exists(engine, ec)) {
        err = "engine script not found: " + mrPath;
        return ESP_ERR_ARG;
    }
    const fs::path es = fs::absolute(fs::u8path(esDir), ec);
    if (ec || !fs::exists(es / "engine_sim.mr", ec)) {
        err = "es library not found (no engine_sim.mr in " + esDir + ")";
        return ESP_ERR_ARG;
    }
    for (char c : topNode) {
        if (!(isalnum(static_cast<unsigned char>(c)) || c == '_')) {
            err = "top node must be an identifier: " + topNode;
            return ESP_ERR_ARG;
        }
    }
    fs::path tmp = tempDir.empty() ? fs::temp_directory_path(ec) / "esp-scripts" : fs::u8path(tempDir);
    fs::create_directories(tmp, ec);
    const fs::path wrapper =
        tmp / ("esp_wrap_" + std::to_string(GetCurrentProcessId()) + "_" + std::to_string(g_wrapperCounter++) + ".mr");
    {
        std::ofstream w(wrapper, std::ios::binary);
        w << "import \"engine_sim.mr\"\n";
        w << "import \"" << forwardSlashes(engine.string()) << "\"\n";
        if (topNode.empty()) w << "main()\n";
        else w << "set_engine(" << topNode << "())\n";
        if (!w) {
            err = "cannot write the wrapper script " + wrapper.string();
            return ESP_ERR_ARG;
        }
    }

    std::lock_guard<std::mutex> lk(g_compileLock);
    *es_script::Compiler::output() = es_script::Compiler::Output();  // the static is never reset upstream

    es_script::LanguageRules rules;
    piranha::Compiler compiler(&rules);
    compiler.setFileExtension(".mr");
    compiler.addSearchPath(forwardSlashes(es.string()) + "/");  // absolute; upstream adds CWD-relative paths
    rules.initialize();

    int32_t result = ESP_OK;
    piranha::NodeProgram program;
    bool built = false;
    piranha::IrCompilationUnit *unit = compiler.compile(wrapper.string());
    if (unit == nullptr) {
        err = "piranha could not open " + wrapper.string();
        result = ESP_ERR_COMPILE;
    } else if (compiler.getErrorList()->getErrorCount() > 0) {
        const piranha::ErrorList *errors = compiler.getErrorList();
        err = std::to_string(errors->getErrorCount()) + " compile error(s): ";
        for (int i = 0; i < errors->getErrorCount() && i < 8; ++i) {
            if (i > 0) err += "; ";
            err += describe(errors->getCompilationError(i));
        }
        result = ESP_ERR_COMPILE;
    } else {
        unit->build(&program);
        program.initialize();
        built = true;
        if (!program.execute()) {
            err = "runtime error: " + program.getRuntimeError();
            result = ESP_ERR_COMPILE;
        }
    }

    const es_script::Compiler::Output o = *es_script::Compiler::output();
    *es_script::Compiler::output() = es_script::Compiler::Output();
    if (built) program.free();
    compiler.free();
    fs::remove(wrapper, ec);

    if (result == ESP_OK && o.engine == nullptr) {
        err = topNode.empty() ? "the script built no engine (no main() calling set_engine?)"
                              : "set_engine(" + topNode + "()) built no engine";
        result = ESP_ERR_COMPILE;
    }
    if (result != ESP_OK) {
        // what a failed script made is leaked rather than freed half-built (rare; Dev builds only)
        return result;
    }
    out.engine = o.engine;
    out.vehicle = o.vehicle;
    out.transmission = o.transmission;
    out.name = o.engine->getName();
    out.sourceKind = 2;
    out.peakTorqueHint = 0.0;
    out.idleRpmHint = 800.0;
    logf(LogLevel::Info, "compiled %s (%zu functions)", engine.filename().string().c_str(), o.functions.size());
    return ESP_OK;
}

} // namespace esp
