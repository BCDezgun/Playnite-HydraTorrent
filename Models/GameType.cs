using System;

namespace HydraTorrent.Models
{
    /// <summary>
    /// Тип загруженной игры
    /// </summary>
    public enum GameType
    {
        /// <summary>
        /// Репак — требуется установка (есть setup.exe, install.exe и т.д.)
        /// </summary>
        Repack,

        /// <summary>
        /// Портативная (предустановленная) версия — можно запускать сразу
        /// </summary>
        Portable,

        /// <summary>
        /// Не удалось определить тип
        /// </summary>
        Unknown
    }
}
