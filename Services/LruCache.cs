using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;

namespace HydraTorrent.Services
{
    /// <summary>
    /// LRU (Least Recently Used) кэш с ограничением размера.
    /// Автоматически удаляет наименее используемые элементы при достижении лимита.
    /// </summary>
    public class LruCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
        private readonly LinkedList<CacheItem> _lruList;
        private readonly object _lock = new object();

        private class CacheItem
        {
            public TKey Key { get; set; }
            public TValue Value { get; set; }
        }

        public LruCache(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be greater than 0", nameof(capacity));

            _capacity = capacity;
            _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
            _lruList = new LinkedList<CacheItem>();
        }

        /// <summary>
        /// Получает значение из кэша. Если найдено, перемещает элемент в начало списка (как недавно использованный).
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    // Перемещаем в начало списка (самый свежий)
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }

                value = default;
                return false;
            }
        }

        /// <summary>
        /// Добавляет или обновляет значение в кэше.
        /// Если кэш полон, удаляет наименее используемый элемент.
        /// </summary>
        public void Add(TKey key, TValue value)
        {
            lock (_lock)
            {
                // Если элемент уже есть, обновляем его
                if (_cache.TryGetValue(key, out var existingNode))
                {
                    _lruList.Remove(existingNode);
                    existingNode.Value.Value = value;
                    _lruList.AddFirst(existingNode);
                    return;
                }

                // Если кэш полон, удаляем самый старый элемент
                if (_cache.Count >= _capacity)
                {
                    var last = _lruList.Last;
                    if (last != null)
                    {
                        _lruList.RemoveLast();
                        _cache.Remove(last.Value.Key);

                        // Освобождаем ресурсы для BitmapImage
                        DisposeValue(last.Value.Value);
                    }
                }

                // Добавляем новый элемент
                var newItem = new CacheItem { Key = key, Value = value };
                var newNode = _lruList.AddFirst(newItem);
                _cache[key] = newNode;
            }
        }

        /// <summary>
        /// Удаляет элемент из кэша
        /// </summary>
        public bool Remove(TKey key)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _cache.Remove(key);
                    DisposeValue(node.Value.Value);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Очищает весь кэш
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                foreach (var node in _lruList)
                {
                    DisposeValue(node.Value);
                }
                _lruList.Clear();
                _cache.Clear();
            }
        }

        /// <summary>
        /// Возвращает текущее количество элементов в кэше
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _cache.Count;
                }
            }
        }

        /// <summary>
        /// Возвращает максимальную вместимость кэша
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Освобождает ресурсы для значения (специальная обработка для BitmapImage)
        /// </summary>
        private void DisposeValue(TValue value)
        {
            if (value is BitmapImage bitmapImage)
            {
                try
                {
                    // Освобождаем StreamSource если есть
                    bitmapImage.StreamSource?.Dispose();
                }
                catch
                {
                    // Игнорируем ошибки при освобождении
                }
            }
            else if (value is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Игнорируем ошибки при освобождении
                }
            }
        }

        /// <summary>
        /// Возвращает все ключи в кэше (от самого свежего к самому старому)
        /// </summary>
        public IEnumerable<TKey> GetKeys()
        {
            lock (_lock)
            {
                return _lruList.Select(item => item.Key).ToList();
            }
        }

        /// <summary>
        /// Проверяет, содержится ли ключ в кэше
        /// </summary>
        public bool ContainsKey(TKey key)
        {
            lock (_lock)
            {
                return _cache.ContainsKey(key);
            }
        }

        /// <summary>
        /// Индексатор для доступа к элементам кэша
        /// </summary>
        public TValue this[TKey key]
        {
            get
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var node))
                    {
                        // Перемещаем в начало списка
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                        return node.Value.Value;
                    }
                    throw new KeyNotFoundException($"Key '{key}' not found in cache");
                }
            }
            set
            {
                Add(key, value);
            }
        }
    }
}
