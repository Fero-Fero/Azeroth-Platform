// Extracts every file from a WoW MPQ (except (listfile)/(attributes)/(signature))
// into an output directory, preserving internal paths.
//
// Usage: exmpq <archive.mpq> <outdir>

#include <StormLib.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <sys/stat.h>
#include <sys/types.h>

static bool IsInternalMeta(const char* name)
{
    return std::strcmp(name, "(listfile)") == 0
        || std::strcmp(name, "(attributes)") == 0
        || std::strcmp(name, "(signature)") == 0
        || std::strcmp(name, "(patch_metadata)") == 0;
}

static void MkdirP(const std::string& path)
{
    std::string cur;
    for (size_t i = 0; i < path.size(); ++i)
    {
        const char c = path[i];
        if (c == '/' || c == '\\')
        {
            if (!cur.empty())
                mkdir(cur.c_str(), 0755);
        }
        cur.push_back(c == '\\' ? '/' : c);
    }
    if (!cur.empty())
        mkdir(cur.c_str(), 0755);
}

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        std::fprintf(stderr, "usage: exmpq <archive.mpq> <outdir>\n");
        return 2;
    }

    const char* archive = argv[1];
    const std::string outDir = argv[2];
    mkdir(outDir.c_str(), 0755);

    HANDLE hMpq = nullptr;
    if (!SFileOpenArchive(archive, 0, MPQ_OPEN_READ_ONLY, &hMpq))
    {
        std::fprintf(stderr, "SFileOpenArchive failed (err %u)\n", (unsigned)GetLastError());
        return 4;
    }

    SFILE_FIND_DATA find{};
    HANDLE hFind = SFileFindFirstFile(hMpq, "*", &find, nullptr);
    if (!hFind)
    {
        SFileCloseArchive(hMpq);
        std::fprintf(stderr, "SFileFindFirstFile failed (err %u)\n", (unsigned)GetLastError());
        return 5;
    }

    int extracted = 0;
    do
    {
        if (IsInternalMeta(find.cFileName))
            continue;

        std::string relative = find.cFileName;
        for (char& c : relative)
        {
            if (c == '\\')
                c = '/';
        }

        const std::string dest = outDir + "/" + relative;
        const auto slash = dest.find_last_of('/');
        if (slash != std::string::npos)
            MkdirP(dest.substr(0, slash));

        if (!SFileExtractFile(hMpq, find.cFileName, dest.c_str(), 0))
        {
            std::fprintf(stderr, "failed to extract '%s' (err %u)\n", find.cFileName, (unsigned)GetLastError());
            SFileFindClose(hFind);
            SFileCloseArchive(hMpq);
            return 6;
        }
        ++extracted;
    } while (SFileFindNextFile(hFind, &find));

    SFileFindClose(hFind);
    SFileCloseArchive(hMpq);
    std::printf("Extracted %d file(s) from %s\n", extracted, archive);
    return 0;
}
