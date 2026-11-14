// Loads the interpreted assemblies at IL2CPP startup, before Unity opens its script assemblies.
//
// Unity binds the scripts on objects in the game's built-in scenes to their classes by assembly
// name when it starts. Loading the game's DLLs here, at the end of il2cpp::vm::Runtime::Init,
// means that by then "Main", "HMLib", ... already exist (as HybridCLR interpreter assemblies),
// so the built-in scenes work unchanged.
//
// Folders (each overridable with an environment variable for testing):
//   <aot dir>    HCLR_AOT_DIR   stripped AOT assemblies: HybridCLR's supplementary metadata,
//                               which lets interpreted code use AOT generics with its own types
//   <hot dirs>   HCLR_HOT_DIRS  ':'-separated; the first folder holding <name>.dll wins
//   hot_names.txt in <aot dir>/..  the assemblies to interpret, one name per line
// Defaults: hclr_port_dirs() (provided by the app), else <il2cpp data dir>/HybridCLR/{aot,hot}
// plus <il2cpp data dir>/../Managed.
#include "il2cpp-config.h"
#include "utils/Runtime.h"
#include "utils/Logging.h"
#include "vm/Assembly.h"
#include "vm/Exception.h"
#include "il2cpp-object-internals.h"
#include "utils/StringUtils.h"
#include "hybridclr/metadata/Assembly.h"

#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string>
#include <vector>

// The app may say where things are (iOS: the app bundle for our files, Documents for the game).
// Return false to use the defaults.
extern "C" bool hclr_port_dirs(std::string* aotDir, std::string* hotDirs) __attribute__((weak));

namespace hybridclr_port
{
    static void Log(const std::string& s)
    {
        std::string m = "[HybridCLR port] " + s;
        il2cpp::utils::Logging::Write("%s", m.c_str());
        fprintf(stderr, "%s\n", m.c_str());
    }

    static bool ReadFile(const std::string& path, std::vector<char>& out)
    {
        FILE* f = fopen(path.c_str(), "rb");
        if (!f) return false;
        fseek(f, 0, SEEK_END);
        long n = ftell(f);
        fseek(f, 0, SEEK_SET);
        out.resize(n > 0 ? (size_t)n : 0);
        bool ok = n <= 0 || fread(out.data(), 1, (size_t)n, f) == (size_t)n;
        fclose(f);
        return ok;
    }

    static std::vector<std::string> Split(const std::string& s, char sep)
    {
        std::vector<std::string> r;
        size_t a = 0;
        while (a <= s.size())
        {
            size_t b = s.find(sep, a);
            if (b == std::string::npos) b = s.size();
            std::string t = s.substr(a, b - a);
            while (!t.empty() && (t.back() == '\r' || t.back() == ' ')) t.pop_back();
            if (!t.empty()) r.push_back(t);
            a = b + 1;
        }
        return r;
    }

    void LoadAssemblies()
    {
        std::string data = il2cpp::utils::Runtime::GetDataDir();
        std::string aotDir = data + "/HybridCLR/aot";
        std::string hotDirs = data + "/HybridCLR/hot:" + data + "/../Managed";
        if (hclr_port_dirs) hclr_port_dirs(&aotDir, &hotDirs);
        if (const char* e = getenv("HCLR_AOT_DIR")) aotDir = e;
        if (const char* e = getenv("HCLR_HOT_DIRS")) hotDirs = e;

        // 1. supplementary metadata for the AOT assemblies
        int aot = 0;
        if (DIR* d = opendir(aotDir.c_str()))
        {
            while (dirent* e = readdir(d))
            {
                std::string n = e->d_name;
                if (n.size() < 5 || n.compare(n.size() - 4, 4, ".dll") != 0) continue;
                std::vector<char> bytes;
                if (!ReadFile(aotDir + "/" + n, bytes)) continue;
                auto err = hybridclr::metadata::Assembly::LoadMetadataForAOTAssembly(bytes.data(), (uint32_t)bytes.size(),
                    hybridclr::metadata::HomologousImageMode::SUPERSET);
                if (err == hybridclr::metadata::LoadImageErrorCode::OK) aot++;
                else Log("AOT metadata " + n + ": error " + std::to_string((int)err));
            }
            closedir(d);
        }
        else Log("no AOT metadata folder " + aotDir);

        // 2. the interpreted assemblies
        std::vector<char> list;
        if (!ReadFile(aotDir + "/../hot_names.txt", list))
        {
            Log("no hot_names.txt next to " + aotDir + ": nothing to interpret");
            return;
        }
        std::vector<std::string> dirs = Split(hotDirs, ':');
        int hot = 0, missing = 0, failed = 0;
        for (const std::string& name : Split(std::string(list.begin(), list.end()), '\n'))
        {
            if (il2cpp::vm::Assembly::GetLoadedAssembly(name.c_str())) continue;
            std::vector<char> bytes;
            std::string from;
            for (const std::string& dir : dirs)
                if (ReadFile(dir + "/" + name + ".dll", bytes)) { from = dir; break; }
            if (from.empty()) { Log("missing " + name + ".dll"); missing++; continue; }
            if (getenv("HCLR_VERBOSE")) Log("loading " + name + " from " + from);
            try
            {
                hybridclr::metadata::Assembly::LoadFromBytes(bytes.data(), bytes.size(), nullptr, 0);
                hot++;
            }
            catch (Il2CppExceptionWrapper& e)
            {
                std::string msg = "?";
                if (e.ex && e.ex->message)
                    msg = il2cpp::utils::StringUtils::Utf16ToUtf8(e.ex->message->chars, e.ex->message->length);
                std::string type = e.ex && ((Il2CppObject*)e.ex)->klass ? ((Il2CppObject*)e.ex)->klass->name : "?";
                Log("failed to load " + name + ": " + type + ": " + msg);
                failed++;
            }
        }
        Log("AOT metadata: " + std::to_string(aot) + ", interpreted assemblies: " + std::to_string(hot) +
            (missing ? ", missing: " + std::to_string(missing) : std::string()) +
            (failed ? ", failed: " + std::to_string(failed) : std::string()));
    }
}
