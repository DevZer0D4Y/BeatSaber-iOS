// Batch DXBC -> Metal translator built on HLSLcc.
// stdin : repeated jobs  [u32 flags][u32 size][size bytes of DXBC (with RDEF)]
// stdout: repeated replies [u32 size][size bytes of JSON]
// The JSON holds the MSL source and the reflection callbacks, which the Python
// side turns into Unity Metal sub-program metadata.
#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>
#include <sstream>
#include "hlslcc.h"

static std::string esc(const std::string& s) {
    std::string o; o.reserve(s.size() + 16);
    for (unsigned char c : s) {
        switch (c) {
        case '"': o += "\\\""; break;
        case '\\': o += "\\\\"; break;
        case '\n': o += "\\n"; break;
        case '\r': o += "\\r"; break;
        case '\t': o += "\\t"; break;
        default:
            if (c < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", c); o += b; }
            else o += (char)c;
        }
    }
    return o;
}

struct Refl : HLSLccReflection {
    std::ostringstream inputs, cbufs, consts, cbbind, tex, bufs, outs, diag;
    bool fi = true, fc = true, fk = true, fb = true, ft = true, fu = true, fo = true, fd = true;
    std::string curCb;
    static void sep(std::ostringstream& s, bool& first) { if (!first) s << ","; first = false; }
    void OnDiagnostics(const std::string& e, int line, bool isError) override {
        sep(diag, fd); diag << "[\"" << esc(e) << "\"," << line << "," << (isError ? 1 : 0) << "]";
    }
    void OnInputBinding(const std::string& name, int idx) override {
        sep(inputs, fi); inputs << "[\"" << esc(name) << "\"," << idx << "]";
    }
    bool OnConstantBuffer(const std::string& name, size_t size, size_t count) override {
        sep(cbufs, fc); cbufs << "[\"" << esc(name) << "\"," << size << "," << count << "]";
        curCb = name; return true;
    }
    bool OnConstant(const std::string& name, int bindIndex, SHADER_VARIABLE_TYPE t, int rows, int cols,
                    bool isMatrix, int arraySize, bool isUsed) override {
        sep(consts, fk);
        consts << "[\"" << esc(curCb) << "\",\"" << esc(name) << "\"," << bindIndex << "," << (int)t << ","
               << rows << "," << cols << "," << (isMatrix ? 1 : 0) << "," << arraySize << "," << (isUsed ? 1 : 0) << "]";
        return true;
    }
    void OnConstantBufferBinding(const std::string& name, int idx) override {
        sep(cbbind, fb); cbbind << "[\"" << esc(name) << "\"," << idx << "]";
    }
    void OnTextureBinding(const std::string& name, int idx, int sampler, bool ms, HLSLCC_TEX_DIMENSION dim, bool uav) override {
        sep(tex, ft); tex << "[\"" << esc(name) << "\"," << idx << "," << sampler << "," << (ms ? 1 : 0) << "," << (int)dim << "," << (uav ? 1 : 0) << "]";
    }
    void OnBufferBinding(const std::string& name, int idx, bool uav) override {
        sep(bufs, fu); bufs << "[\"" << esc(name) << "\"," << idx << "," << (uav ? 1 : 0) << "]";
    }
    void OnVertexProgramOutput(const std::string& name, const std::string& sem, int semIdx) override {
        sep(outs, fo); outs << "[\"" << esc(name) << "\",\"" << esc(sem) << "\"," << semIdx << "]";
    }
};

static bool readAll(void* p, size_t n) { return fread(p, 1, n, stdin) == n; }

int main() {
    uint32_t flags, size;
    std::vector<char> buf;
    while (readAll(&flags, 4) && readAll(&size, 4)) {
        buf.resize(size);
        if (!readAll(buf.data(), size)) break;
        Refl r;
        GlExtensions ext = {};
        GLSLCrossDependencyData deps;
        HLSLccSamplerPrecisionInfo prec;
        GLSLShader out;
        int ok = TranslateHLSLFromMem(buf.data(), flags, LANG_METAL, &ext, &deps, prec, r, &out);
        std::ostringstream j;
        j << "{\"ok\":" << ok << ",\"type\":" << out.shaderType
          << ",\"msl\":\"" << esc(out.sourceCode) << "\""
          << ",\"inputs\":[" << r.inputs.str() << "]"
          << ",\"cbuffers\":[" << r.cbufs.str() << "]"
          << ",\"consts\":[" << r.consts.str() << "]"
          << ",\"cbbind\":[" << r.cbbind.str() << "]"
          << ",\"textures\":[" << r.tex.str() << "]"
          << ",\"buffers\":[" << r.bufs.str() << "]"
          << ",\"outputs\":[" << r.outs.str() << "]"
          << ",\"diag\":[" << r.diag.str() << "]}";
        std::string s = j.str();
        uint32_t n = (uint32_t)s.size();
        fwrite(&n, 4, 1, stdout);
        fwrite(s.data(), 1, n, stdout);
        fflush(stdout);
    }
    return 0;
}
