package com.clipfx.share.core

/** Sorts received files into folders by type. Must match the Windows app. */
object Categories {
    const val FOLDERS = "Folders"

    private val map = listOf(
        "Videos" to "mp4 mkv mov avi wmv flv webm m4v 3gp 3g2 ts mts m2ts mpeg mpg vob ogv rmvb",
        "Images" to "jpg jpeg png gif webp heic heif bmp tif tiff svg ico avif raw dng cr2 cr3 nef arw orf rw2 psd",
        "Music" to "mp3 m4a aac flac wav ogg oga opus wma amr mid midi aiff alac ape",
        "Documents" to "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf csv md epub mobi pages numbers key json xml html htm log",
        "Apps" to "apk apks xapk apkm aab exe msi msix msixbundle appx appxbundle dmg pkg deb rpm appimage jar bat cmd",
        "Archives" to "zip rar 7z tar gz tgz bz2 tbz2 xz txz zst lz lzma cab iso img z arj",
    )
    private val byExt: Map<String, String> = map.flatMap { (name, exts) -> exts.split(' ').map { it to name } }.toMap()

    fun of(fileName: String): String {
        val dot = fileName.lastIndexOf('.')
        if (dot < 0 || dot == fileName.length - 1) return "Other"
        return byExt[fileName.substring(dot + 1).lowercase()] ?: "Other"
    }

    fun place(relativePath: String, type: ItemType, organize: Boolean): String {
        if (!organize || type == ItemType.TEXT) return relativePath
        val p = relativePath.replace('\\', '/')
        val insideFolder = p.contains('/') || type == ItemType.DIR
        return if (insideFolder) "$FOLDERS/$p" else "${of(p)}/$p"
    }
}
