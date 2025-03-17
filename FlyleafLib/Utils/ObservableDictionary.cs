using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace FlyleafLib;

public static partial class Utils
{
    public class ObservableDictionary<TKey, TVal> : Dictionary<TKey, TVal>, INotifyPropertyChanged, INotifyCollectionChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public event NotifyCollectionChangedEventHandler CollectionChanged;

        public new TVal this[TKey key]
        {
            get => base[key];

            set
            {
                if (ContainsKey(key) && base[key].Equals(value)) return;

                if (CollectionChanged != null)
                {
                    KeyValuePair<TKey, TVal> oldItem = new(key, base[key]);
                    KeyValuePair<TKey, TVal> newItem = new(key, value);
                    base[key] = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key.ToString()));
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newItem, oldItem, this.ToList().IndexOf(newItem)));
                }
                else
                {
                    base[key] = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key.ToString()));
                }
            }
        }
    }
}
