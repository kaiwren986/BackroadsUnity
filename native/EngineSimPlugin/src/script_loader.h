// script_loader.h: piranha .mr engine scripts (PLAN 3.2, research 01 5.4, App. A.6). Built with ESP_WITH_PIRANHA.
//
// Loader rules: one global compile mutex (es_script::Compiler::output() is a process-wide static); the output is
// reset before every compile; our own piranha::Compiler over es_script::LanguageRules with an ABSOLUTE search path to
// the es library (upstream's es_script::Compiler adds CWD-relative paths and writes error_log.log into the CWD);
// a generated wrapper .mr in the temp folder imports engine_sim.mr and the engine file, then calls main() or, for
// older scripts without main, set_engine(<top node>()).
#pragma once

#include "builders/builders.h"

#include <cstdint>
#include <string>

namespace esp {

// Returns ESP_OK and fills out, or ESP_ERR_COMPILE / ESP_ERR_ARG with err set.
int32_t compileEngineScript(const std::string &mrPath, const std::string &topNode, const std::string &esDir,
                            const std::string &tempDir, EngineBundle &out, std::string &err);

// Writes a shippable copy of an .mr file (mr_sanitise.cpp).
int32_t sanitiseMr(const std::string &src, const std::string &dst, const std::string &fictionalId, std::string &err);

} // namespace esp
