﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace TrackableEntities.Client
{
    /// <summary>
    /// Collection responsible for tracking changes to entities.
    /// </summary>
    /// <typeparam name="T">Trackable entity type</typeparam>
    public class ChangeTrackingCollection<T> : ObservableCollection<T>, 
        ITrackingCollection<T>, ITrackingCollection
        where T : class, ITrackable, INotifyPropertyChanged
    {
        // Deleted entities cache
        readonly private Collection<T> _deletedEntities = new Collection<T>();

        /// <summary>
        /// Event for when an entity in the collection has changed its tracking state.
        /// </summary>
        public event EventHandler EntityChanged;

        /// <summary>
        /// Default contstructor with change-tracking disabled
        /// </summary>
        public ChangeTrackingCollection() : this(false) { }

        /// <summary>
        /// Change-tracking will not begin after entities are added, 
        /// unless tracking is enabled.
        /// </summary>
        /// <param name="enableTracking">Enable tracking after entities are added</param>
        public ChangeTrackingCollection(bool enableTracking)
        {
            Tracking = enableTracking;
        }

        /// <summary>
        /// Constructor that accepts a collection of entities.
        /// Change-tracking will begin after entities are added, 
        /// unless tracking is disabled.
        /// </summary>
        /// <param name="entities">Entities being change-tracked</param>
        /// <param name="disableTracking">Disable tracking after entities are added</param>
        public ChangeTrackingCollection(IEnumerable<T> entities, bool disableTracking = false)
        {
            // Add items to the change tracking list
            foreach (T item in entities)
            {
                Add(item);
            }
            Tracking = !disableTracking;
        }

        /// <summary>
        /// Constructor that accepts one or more entities.
        /// Change-tracking will begin after entities are added.
        /// </summary>
        /// <param name="entities">Entities being change-tracked</param>
        public ChangeTrackingCollection(params T[] entities)
            : this(entities, false) { }

        /// <summary>
        /// Turn change-tracking on and off.
        /// </summary>
        public bool Tracking
        {
            get
            {
                return _tracking;
            }
            set
            {
                // Get notified when an item in the collection has changed
                foreach (T item in this)
                {
                    // Property change notification
                    if (value) item.PropertyChanged += OnPropertyChanged;
                    else item.PropertyChanged -= OnPropertyChanged;

                    // Enable tracking on trackable collection properties
                    item.SetTracking(value);
                }
                _tracking = value;
            }
        }
        private bool _tracking;

        // Fired when an item in the collection has changed
        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (Tracking)
            {
                var entity = sender as ITrackable;
                if (entity != null
                    && e.PropertyName != Constants.TrackingProperties.TrackingState
                    && e.PropertyName != Constants.TrackingProperties.ModifiedProperties)
                {
                    // Mark item as updated, add prop to modified props, and fire EntityChanged event
                    if (entity.TrackingState == TrackingState.Unchanged)
                    {
                        entity.TrackingState = TrackingState.Modified;
                        if (entity.ModifiedProperties == null)
                            entity.ModifiedProperties = new List<string>();
                        if (!entity.ModifiedProperties.Contains(e.PropertyName))
                            entity.ModifiedProperties.Add(e.PropertyName);
                        if (EntityChanged != null) EntityChanged(this, EventArgs.Empty);
                    }
                }
            }
        }

        /// <summary>
        /// Insert item at specified index.
        /// </summary>
        /// <param name="index">Zero-based index at which item should be inserted</param>
        /// <param name="item">Item to insert</param>
        protected override void InsertItem(int index, T item)
        {
            if (Tracking)
            {
                // Mark item as added, listen for property changes
                item.TrackingState = TrackingState.Added;
                item.PropertyChanged += OnPropertyChanged;

                // Enable tracking on trackable collection properties
                item.SetTracking(Tracking);

                // Mark items as added in trackable collection properties
                item.SetState(TrackingState.Added);

                // Fire EntityChanged event
                if (EntityChanged != null) EntityChanged(this, EventArgs.Empty);
            }
            base.InsertItem(index, item);
        }

        /// <summary>
        /// Remove item at specified index.
        /// </summary>
        /// <param name="index">Zero-based index at which item should be removed</param>
        protected override void RemoveItem(int index)
        {
            // Mark existing item as deleted, stop listening for property changes,
            // then fire EntityChanged event, and cache item.
            if (Tracking)
            {
                // Removing added item should set it to unchanged.
                T item = Items[index];
                if (item.TrackingState == TrackingState.Added)
                {
                    item.TrackingState = TrackingState.Unchanged;
                    item.ModifiedProperties = null;
                    item.PropertyChanged -= OnPropertyChanged;
                    item.SetState(TrackingState.Unchanged);
                    item.SetModifiedProperties(null);
                    if (EntityChanged != null) EntityChanged(this, EventArgs.Empty);
                }

                // Removing unchanged or modified item should mark as deleted.
                // Removing deleted item should have no effect.
                else if (item.TrackingState != TrackingState.Deleted)
                {
                    item.TrackingState = TrackingState.Deleted;
                    item.ModifiedProperties = null;
                    item.PropertyChanged -= OnPropertyChanged;
                    item.SetState(TrackingState.Deleted);
                    item.SetModifiedProperties(null);
                    if (EntityChanged != null) EntityChanged(this, EventArgs.Empty);
                    _deletedEntities.Add(item);
                }
            }
            base.RemoveItem(index);
        }

        /// <summary>
        /// Get entities that have been added, modified or deleted, including child 
        /// collections with entities that have been added, modified or deleted.
        /// </summary>
        /// <returns>Collection containing only changed entities</returns>
        public ITrackingCollection<T> GetChanges()
        {
            // Get changed items in this tracking collection, 
            // including items with child collections that have changes
            var changes = ((ITrackingCollection)this).GetChanges().Cast<T>()
                .Union(this.Where(
                       t => t.TrackingState == TrackingState.Unchanged &&
                       t.HasChanges()))
                .ToList();

            // Temporarily restore deletes to changes
            foreach (var change in changes)
            {
                change.RestoreDeletes();
            }

            // Clone changes
            var items = changes.Select(t => t.Clone<T>()).ToList();

            // Remove deletes from changes and re-cache
            foreach (var change in changes)
            {
                change.RemoveDeletes(true);
            }

            // Set collection properties to include only changed items
            foreach (T item in items)
            {
                item.SetChanges();
            }

            // Return a new generic tracking collection
            return new ChangeTrackingCollection<T>(items, true);
        }

        /// <summary>
        /// Get entities that have been added, modified or deleted.
        /// </summary>
        /// <returns>Collection containing only changed entities</returns>
        ITrackingCollection ITrackingCollection.GetChanges()
        {
            // Get changed items in this tracking collection
            var changes = (from existing in this
                           where existing.TrackingState != TrackingState.Unchanged
                           select existing)
                          .Union(_deletedEntities);

            // Return a new generic tracking collection
            return new ChangeTrackingCollection<T>(changes, true);
        }
    }
}