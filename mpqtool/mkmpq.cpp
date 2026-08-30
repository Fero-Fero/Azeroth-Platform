// Minimal MPQ builder for the Azeroth Platform patch-D pipeline.
//
// Creates a WoW 3.3.5a-compatible (MPQ v1) archive and adds every regular file from a source
// directory under a given internal prefix (e.g. DBFilesClient\Foo.dbc). We deliberately do NOT
// create an (attributes) file: it stores per-file MD5s that the 3.3.5a client doesn't need, and
// generating it is what tripped the assertion in Debian's old smpq/StormLib 9.22.
//
// Usage: mkmpq <out.mpq> <srcdir> [internalPrefix]

#include <StormLib.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <dirent.h>
#include <sys/stat.h>

static bool DirExists(const char* path)
{
    struct stat st;
    return stat(path, &st) == 0 && S_ISDIR(st.st_mode);
}

static void CollectFiles(const std::string& dir, const std::string& relative, std::vector<std::string>& files)
{
    DIR* d = opendir(dir.c_str());
    if (!d)
        return;

    while (struct dirent* ent = readdir(d))
    {
        if (ent->d_name[0] == '.')
            continue;
        const std::string name = ent->d_name;
        const std::string full = dir + "/" + name;
        const std::string rel = relative.empty() ? name : relative + "/" + name;
        struct stat st;
        if (stat(full.c_str(), &st) != 0)
            continue;
        if (S_ISDIR(st.st_mode))
            CollectFiles(full, rel, files);
        else if (S_ISREG(st.st_mode))
            files.push_back(rel);
    }
    closedir(d);
}

// MPQ v1 hash table size must be a power of two; give a little headroom for the (listfile).
static DWORD NextPow2(size_t n)
{
    DWORD p = 4;
    while (p < n)
        p <<= 1;
    return p;
}

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        std::fprintf(stderr, "usage: mkmpq <out.mpq> <srcdir> [internalPrefix]\n");
        return 2;
    }

    const char* outMpq = argv[1];
    const char* srcDir = argv[2];
    const std::string prefix = (argc > 3) ? argv[3] : "DBFilesClient";

    std::vector<std::string> files;
    CollectFiles(srcDir, "", files);
    if (files.empty() && !DirExists(srcDir))
    {
        std::fprintf(stderr, "cannot open source dir '%s'\n", srcDir);
        return 2;
    }

    if (files.empty())
    {
        std::fprintf(stderr, "no files to package in '%s'\n", srcDir);
        return 3;
    }
    std::sort(files.begin(), files.end());

    std::remove(outMpq);

    HANDLE hMpq = nullptr;
    const DWORD maxFiles = NextPow2(files.size() + 2);
    if (!SFileCreateArchive(outMpq, MPQ_CREATE_ARCHIVE_V1 | MPQ_CREATE_LISTFILE, maxFiles, &hMpq))
    {
        std::fprintf(stderr, "SFileCreateArchive failed (err %u)\n", (unsigned)GetLastError());
        return 4;
    }

    for (const std::string& f : files)
    {
        const std::string local = std::string(srcDir) + "/" + f;
        std::string archived = f;
        for (char& c : archived)
        {
            if (c == '/')
                c = '\\';
        }
        if (!prefix.empty())
            archived = prefix + "\\" + archived;
        if (!SFileAddFileEx(hMpq, local.c_str(), archived.c_str(),
                            MPQ_FILE_COMPRESS | MPQ_FILE_REPLACEEXISTING,
                            MPQ_COMPRESSION_ZLIB, MPQ_COMPRESSION_ZLIB))
        {
            std::fprintf(stderr, "failed to add '%s' (err %u)\n", f.c_str(), (unsigned)GetLastError());
            SFileCloseArchive(hMpq);
            return 5;
        }
    }

    if (!SFileCloseArchive(hMpq))
    {
        std::fprintf(stderr, "SFileCloseArchive failed (err %u)\n", (unsigned)GetLastError());
        return 6;
    }

    std::printf("Created %s with %zu file(s) under %s\\\n", outMpq, files.size(), prefix.c_str());
    return 0;
}
