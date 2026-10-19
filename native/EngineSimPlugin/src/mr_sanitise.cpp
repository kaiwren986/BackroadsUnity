// mr_sanitise.cpp: writes a shippable copy of an engine-sim .mr script (PLAN 3.2 [C2]): comments stripped, every
// `name:` string set to the fictional id, every node defined in the file renamed to <id>_n<k> (references follow;
// `main` keeps its name). Words from the PLAN 3.2 denylist that remain afterwards (references into the es library,
// such as part-library node names) are reported: the copy is written, the call returns ESP_ERR_COMPILE and err lists
// them, so ContentBuild knows the library must be sanitised too before that engine ships.
#include "script_loader.h"

#include "EngineSimPlugin.h"
#include "wav_io.h"

#include <cctype>
#include <cstdio>
#include <map>
#include <regex>
#include <set>
#include <string>
#include <vector>

namespace esp {

namespace {

// PLAN 3.2 Content.NoRealMarks denylist. Longer words match as substrings (case-insensitive); the short ones only as
// whole tokens between non-alphanumerics, so "audio" or "natural" never match.
const char *const kSubstring[] = {"chevrolet", "chev", "subaru", "ferrari", "hayabusa", "harley", "suzuki", "kohler",
                                  "merlin", "toyota", "freightliner", "cascadia", "volvo", "scania", "tatra", "zetros",
                                  "mercedes", "caterpillar", "torsen", "isuzu", "honda", "yaris", "ranger"};
const char *const kToken[] = {"vtec", "gm", "bmw", "audi", "2jz", "rolls", "ford", "hino", "vnl", "ural", "gr86", "d3",
                              "jake", "baja"};

std::string lower(std::string s) {
    for (char &c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

std::string stripComments(const std::string &in) {
    std::string out;
    out.reserve(in.size());
    bool inString = false;
    for (size_t i = 0; i < in.size(); ++i) {
        const char c = in[i];
        if (inString) {
            out += c;
            if (c == '\\' && i + 1 < in.size()) out += in[++i];
            else if (c == '"') inString = false;
            continue;
        }
        if (c == '"') {
            inString = true;
            out += c;
        } else if (c == '/' && i + 1 < in.size() && in[i + 1] == '/') {
            while (i < in.size() && in[i] != '\n') ++i;
            if (i < in.size()) out += '\n';
        } else if (c == '/' && i + 1 < in.size() && in[i + 1] == '*') {
            i += 2;
            while (i + 1 < in.size() && !(in[i] == '*' && in[i + 1] == '/')) {
                if (in[i] == '\n') out += '\n';  // keep line numbers
                ++i;
            }
            ++i;
        } else {
            out += c;
        }
    }
    // drop trailing spaces left by removed comments
    std::string clean;
    size_t lineStart = 0;
    for (size_t i = 0; i <= out.size(); ++i) {
        if (i == out.size() || out[i] == '\n') {
            size_t end = i;
            while (end > lineStart && (out[end - 1] == ' ' || out[end - 1] == '\t' || out[end - 1] == '\r')) --end;
            clean.append(out, lineStart, end - lineStart);
            if (i < out.size()) clean += '\n';
            lineStart = i + 1;
        }
    }
    return clean;
}

std::vector<std::string> remainingMarks(const std::string &text) {
    std::set<std::string> found;
    const std::string low = lower(text);
    for (const char *w : kSubstring)
        if (low.find(w) != std::string::npos) found.insert(w);
    std::string tok;
    for (size_t i = 0; i <= low.size(); ++i) {
        const char c = i < low.size() ? low[i] : ' ';
        if (std::isalnum(static_cast<unsigned char>(c))) {
            tok += c;
        } else if (!tok.empty()) {
            for (const char *w : kToken)
                if (tok == w) found.insert(w);
            tok.clear();
        }
    }
    return std::vector<std::string>(found.begin(), found.end());
}

} // namespace

int32_t sanitiseMr(const std::string &src, const std::string &dst, const std::string &fictionalId, std::string &err) {
    for (char c : fictionalId) {
        if (!(std::islower(static_cast<unsigned char>(c)) || std::isdigit(static_cast<unsigned char>(c)) || c == '_')) {
            err = "fictional id must be lower-case letters, digits and _: " + fictionalId;
            return ESP_ERR_ARG;
        }
    }
    if (!remainingMarks(fictionalId).empty()) {
        err = "the fictional id itself contains a denylisted word";
        return ESP_ERR_ARG;
    }
    std::string text;
    {
        FILE *f = nullptr;
        if (_wfopen_s(&f, widen(src).c_str(), L"rb") != 0 || f == nullptr) {
            err = "cannot open " + src;
            return ESP_ERR_ARG;
        }
        char buf[65536];
        size_t n;
        while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) text.append(buf, n);
        std::fclose(f);
    }
    text = stripComments(text);

    // name: "..." -> name: "<id>"
    text = std::regex_replace(text, std::regex(R"re((\bname\s*:\s*)"[^"]*")re"), "$1\"" + fictionalId + "\"");

    // nodes defined here: [public|private] node <ident> {
    std::map<std::string, std::string> rename;
    {
        const std::regex def(R"(\bnode\s+([A-Za-z_][A-Za-z0-9_]*))");
        int k = 0;
        for (auto it = std::sregex_iterator(text.begin(), text.end(), def); it != std::sregex_iterator(); ++it) {
            const std::string name = (*it)[1].str();
            if (name == "main" || rename.count(name)) continue;
            rename[name] = fictionalId + "_n" + std::to_string(k++);
        }
    }
    if (!rename.empty()) {
        // Rename a defined node's name only where it is used as a node type: after `node`, in a call `X(`, in an
        // instantiation `X instance(`, or as a type in brackets `[X]`. Instance names (often the same word, as in
        // `wires wires()`), member accesses `.x` and parameter names `x:` keep their spelling.
        auto isId = [](char ch) { return std::isalnum(static_cast<unsigned char>(ch)) || ch == '_'; };
        std::string out;
        out.reserve(text.size());
        bool inString = false;
        std::string prevWord;
        char prevChar = '\0';
        for (size_t i = 0; i < text.size();) {
            const char c = text[i];
            if (inString) {
                out += c;
                if (c == '\\' && i + 1 < text.size()) out += text[++i];
                else if (c == '"') inString = false;
                ++i;
                continue;
            }
            if (c == '"') {
                inString = true;
                out += c;
                prevChar = c;
                ++i;
                continue;
            }
            if (std::isalpha(static_cast<unsigned char>(c)) || c == '_') {
                size_t j = i;
                while (j < text.size() && isId(text[j])) ++j;
                const std::string id = text.substr(i, j - i);
                bool typePos = false;
                auto r = rename.find(id);
                if (r != rename.end() && prevChar != '.') {
                    size_t k = j;
                    while (k < text.size() && (text[k] == ' ' || text[k] == '\t')) ++k;
                    if (prevWord == "node" || prevChar == '[') typePos = true;
                    // `X(` is a call unless X is the instance name of a declaration `T X(` (the previous token on the
                    // line is an identifier, as in `wires wires()`)
                    else if (k < text.size() && text[k] == '(') typePos = prevChar != 'a';
                    else if (k < text.size() && (std::isalpha(static_cast<unsigned char>(text[k])) || text[k] == '_')) {
                        size_t m = k;
                        while (m < text.size() && isId(text[m])) ++m;
                        while (m < text.size() && (text[m] == ' ' || text[m] == '\t')) ++m;
                        typePos = m < text.size() && text[m] == '(';
                    }
                }
                out += typePos ? r->second : id;
                prevWord = id;
                prevChar = 'a';
                i = j;
                continue;
            }
            out += c;
            if (c == '\n') {  // statements are line based: a new line starts a new statement
                prevChar = '\n';
                prevWord.clear();
            } else if (c != ' ' && c != '\t' && c != '\r') {
                prevChar = c;
                prevWord.clear();
            }
            ++i;
        }
        text.swap(out);
    }

    {
        FILE *f = nullptr;
        if (_wfopen_s(&f, widen(dst).c_str(), L"wb") != 0 || f == nullptr) {
            err = "cannot write " + dst;
            return ESP_ERR_ARG;
        }
        std::fwrite(text.data(), 1, text.size(), f);
        std::fclose(f);
    }
    const std::vector<std::string> left = remainingMarks(text);
    if (!left.empty()) {
        err = "written, but denylisted words remain (es library references):";
        for (const std::string &w : left) err += " " + w;
        return ESP_ERR_COMPILE;
    }
    err = std::to_string(rename.size()) + " nodes renamed, comments stripped";
    return ESP_OK;
}

} // namespace esp
