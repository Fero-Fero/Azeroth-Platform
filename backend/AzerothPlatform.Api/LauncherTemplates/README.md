# Launcher style templates

Hard-coded visual templates for the desktop launcher. Three templates exist and are selectable on
the website's **Launcher** admin page: `classic`, `tbc`, and `wotlk`.

Each template folder holds the branding assets shipped with the product. Drop your files into the
matching folder and rebuild the image (`docker compose up -d --build`). The files are served to both
the website preview and the launcher.

## Expected files per template

Put one of each (any listed extension) into `classic/`, `tbc/`, and `wotlk/`:

- `background.<png|jpg|jpeg|webp|gif>` - the launcher background. Animated **GIF** or animated
  **WebP** will animate in the launcher.
- `logo.<png|jpg|jpeg|webp>` - the logo shown in the launcher's top-left header.

If a file is missing, the launcher falls back to any background/logo uploaded on the website, then to
no image.

## Notes

- The template also sets an **accent color** (used for the Play button etc.). Accent colors are
  hard-coded per template in `LauncherTemplates.cs` and are not file-based.
- Selecting a template does **not** prevent per-profile background/logo overrides; profile assets
  still take precedence, then website "Default branding" uploads, then the template files here.
