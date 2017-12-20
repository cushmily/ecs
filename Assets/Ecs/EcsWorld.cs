using System;
using System.Collections.Generic;

namespace LeopotamGroup.Ecs {
    public sealed class EcsWorld {
        /// <summary>
        /// Raises on component attaching to entity.
        /// </summary>
        public event Action<IEcsComponent> OnComponentAttach = delegate { };

        /// <summary>
        /// Raises on component detaching from entity.
        /// </summary>
        public event Action<IEcsComponent> OnComponentDetach = delegate { };

        /// <summary>
        /// All registered systems.
        /// </summary>
        readonly List<IEcsSystem> _allSystems = new List<IEcsSystem> (64);

        /// <summary>
        /// Dictionary for fast search component -> type id.
        /// </summary>
        /// <returns></returns>
        readonly Dictionary<Type, int> _componentIds = new Dictionary<Type, int> (64);

        readonly Dictionary<int, EcsComponentPool> _componentPools = new Dictionary<int, EcsComponentPool> (64);

        /// <summary>
        /// List of all entities (their components).
        /// </summary>
        readonly List<EcsEntity> _entities = new List<EcsEntity> (1024);

        /// <summary>
        /// List of removed entities - they can be reused later.
        /// </summary>
        readonly List<int> _reservedEntityIds = new List<int> (256);

        /// <summary>
        /// List of add / remove operations for components on entities.
        /// </summary>
        readonly List<DelayedUpdate> _delayedUpdates = new List<DelayedUpdate> (64);

        /// <summary>
        /// List of requested filters.
        /// </summary>
        readonly List<EcsFilter> _filters = new List<EcsFilter> (64);

        /// <summary>
        /// Is Initialize method was called?
        /// </summary>
        bool _inited;

        /// <summary>
        /// Adds new system to processing.
        /// </summary>
        /// <param name="system">System instance.</param>
        public EcsWorld AddSystem (IEcsSystem system) {
            if (_inited) {
                throw new Exception ("Already initialized, cant add new system.");
            }
            _allSystems.Add (system);
            return this;
        }

        /// <summary>
        /// Closes registration for new external data, initialize all registered systems.
        /// </summary>
        public void Initialize () {
            _inited = true;
            for (int i = 0, iMax = _allSystems.Count; i < iMax; i++) {
                _allSystems[i].Initialize (this);
            }
            ProcessDelayedUpdates ();
        }

        /// <summary>
        /// Destroys all registered external data, full cleanup for internal data.
        /// </summary>
        public void Destroy () {
            for (int i = 0, iMax = _entities.Count; i < iMax; i++) {
                RemoveEntity (i);
            }
            ProcessDelayedUpdates ();

            for (var i = _allSystems.Count - 1; i >= 0; i--) {
                _allSystems[i].Destroy ();
            }

            _allSystems.Clear ();
            _componentIds.Clear ();
            _componentPools.Clear ();
            _entities.Clear ();
            _reservedEntityIds.Clear ();
            _filters.Clear ();
        }

        /// <summary>
        /// Processes all IEcsUpdateSystem systems.
        /// </summary>
        public void Update () {
            for (int i = 0, iMax = _allSystems.Count; i < iMax; i++) {
                var updateSystem = _allSystems[i] as IEcsUpdateSystem;
                if (updateSystem != null) {
                    updateSystem.Update ();
                    ProcessDelayedUpdates ();
                }
            }
            ClearEventFilters ();
            ProcessDelayedUpdates ();
        }

        /// <summary>
        /// Processes all IEcsFixedUpdateSystem systems.
        /// </summary>
        public void FixedUpdate () {
            for (int i = 0, iMax = _allSystems.Count; i < iMax; i++) {
                var updateSystem = _allSystems[i] as IEcsFixedUpdateSystem;
                if (updateSystem != null) {
                    updateSystem.FixedUpdate ();
                    ProcessDelayedUpdates ();
                }
            }
        }

        /// <summary>
        /// Creates new entity.
        /// </summary>
        public int CreateEntity () {
            int entity;
            if (_reservedEntityIds.Count > 0) {
                var id = _reservedEntityIds.Count - 1;
                entity = _reservedEntityIds[id];
                _entities[entity].IsReserved = false;
                _reservedEntityIds.RemoveAt (id);
            } else {
                entity = _entities.Count;
                _entities.Add (new EcsEntity ());
            }
            return entity;
        }

        /// <summary>
        /// Removes exists entity or throws exception on invalid one.
        /// </summary>
        /// <param name="entity">Entity.</param>
        public void RemoveEntity (int entity) {
            if (entity < 0 || entity >= _entities.Count) {
                throw new Exception ("Invalid entity");
            }
            if (!_entities[entity].IsReserved) {
                _delayedUpdates.Add (new DelayedUpdate (DelayedUpdate.Op.RemoveEntity, entity, 0));
            }
        }

        /// <summary>
        /// Adds component to entity.
        /// </summary>
        /// <param name="entity">Entity.</param>
        public T AddComponent<T> (int entity) where T : class, IEcsComponent {
            if (entity < 0 || entity >= _entities.Count) {
                throw new Exception ("Invalid entity");
            }
            var componentId = GetComponentTypeId (typeof (T));
            var entityData = _entities[entity];
            if (entityData.Mask.GetBit (componentId)) {
                return entityData.Components[componentId] as T;
            }
            _delayedUpdates.Add (new DelayedUpdate (DelayedUpdate.Op.AddComponent, entity, componentId));

            EcsComponentPool pool;
            if (!_componentPools.TryGetValue (componentId, out pool)) {
                pool = new EcsComponentPool (typeof (T));
                _componentPools[componentId] = pool;
            }
            var component = pool.Get () as T;
            // TODO: move to arrays.
            while (entityData.Components.Count <= componentId) {
                entityData.Components.Add (null);
            }
            entityData.Components[componentId] = component;
            return component;
        }

        /// <summary>
        /// Removes component from entity.
        /// </summary>
        /// <param name="entity">Entity.</param>
        public void RemoveComponent<T> (int entity) where T : class, IEcsComponent {
            if (entity < 0 || entity >= _entities.Count) {
                throw new Exception ("Invalid entity");
            }
            var componentId = GetComponentTypeId (typeof (T));
            if (componentId != -1) {
                _delayedUpdates.Add (new DelayedUpdate (DelayedUpdate.Op.RemoveComponent, entity, componentId));
            }
        }

        /// <summary>
        /// Removes component from entity.
        /// </summary>
        /// <param name="entity">Entity.</param>
        public T GetComponent<T> (int entity) where T : class, IEcsComponent {
            if (entity < 0 || entity >= _entities.Count) {
                throw new Exception ("Invalid entity");
            }
            var componentId = GetComponentTypeId (typeof (T));
            if (componentId == -1) {
                return null;
            }
            var components = _entities[entity].Components;
            return components.Count <= componentId ? null : components[componentId] as T;
        }

        /// <summary>
        /// Creates event enity with event-data component.
        /// </summary>
        public T CreateEvent<T> () where T : class, IEcsComponent {
            return AddComponent<T> (CreateEntity ());
        }

        /// <summary>
        /// For internal use only, dont call it directly.
        /// </summary>
        public string GetDebugStats () {
            return string.Format ("Components: {0}\nEntitiesInCache: {1}\nFilters: {2}",
                _componentIds.Count, _entities.Count, _filters.Count);
        }

        /// <summary>
        /// Gets component index in EcsEntity.Components list.
        /// </summary>
        /// <param name="componentType">Component type.</param>
        public int GetComponentTypeId (Type componentType) {
            int retVal;
            if (!_componentIds.TryGetValue (componentType, out retVal)) {
                retVal = _componentIds.Count;
                _componentIds[componentType] = retVal;
            }
            return retVal;
        }

        /// <summary>
        /// Gets filter for specific component.
        /// </summary>
        /// <param name="forEvents">Filter will be used for events.</param>
        public EcsFilter GetFilter<A> (bool forEvents) {
            var mask = new EcsComponentMask (GetComponentTypeId (typeof (A)));
            return GetFilter (mask, forEvents);
        }

        /// <summary>
        /// Gets filter for specific components.
        /// </summary>
        /// <param name="forEvents">Filter will be used for events.</param>
        public EcsFilter GetFilter<A, B> (bool forEvents) {
            var mask = new EcsComponentMask ();
            mask.SetBit (GetComponentTypeId (typeof (A)), true);
            mask.SetBit (GetComponentTypeId (typeof (B)), true);
            return GetFilter (mask, forEvents);
        }

        /// <summary>
        /// Gets filter for specific components.
        /// </summary>
        /// <param name="forEvents">Filter will be used for events.</param>
        public EcsFilter GetFilter<A, B, C> (bool forEvents) {
            var mask = new EcsComponentMask ();
            mask.SetBit (GetComponentTypeId (typeof (A)), true);
            mask.SetBit (GetComponentTypeId (typeof (B)), true);
            mask.SetBit (GetComponentTypeId (typeof (C)), true);
            return GetFilter (mask, forEvents);
        }

        /// <summary>
        /// Gets filter for specific components.
        /// </summary>
        /// <param name="forEvents">Filter will be used for events.</param>
        public EcsFilter GetFilter<A, B, C, D> (bool forEvents) {
            var mask = new EcsComponentMask ();
            mask.SetBit (GetComponentTypeId (typeof (A)), true);
            mask.SetBit (GetComponentTypeId (typeof (B)), true);
            mask.SetBit (GetComponentTypeId (typeof (C)), true);
            mask.SetBit (GetComponentTypeId (typeof (D)), true);
            return GetFilter (mask, forEvents);
        }

        /// <summary>
        /// Gets filter for specific components.
        /// </summary>
        /// <param name="mask">Component selection.</param>
        /// <param name="forEvents">Filter will be used for events.</param>
        public EcsFilter GetFilter (EcsComponentMask mask, bool forEvents) {
            var i = _filters.Count - 1;
            for (; i >= 0; i--) {
                if (_filters[i].ForEvents == forEvents && _filters[i].Mask.IsEquals (mask)) {
                    break;
                }
            }
            if (i == -1) {
                i = _filters.Count;
                _filters.Add (new EcsFilter (mask, forEvents));
            }
            return _filters[i];
        }

        /// <summary>
        /// Recycle all filtered event entities.
        /// </summary>
        void ClearEventFilters () {
            for (var i = _filters.Count - 1; i >= 0; i--) {
                if (_filters[i].ForEvents) {
                    var list = _filters[i].Entities;
                    for (var j = list.Count - 1; j >= 0; j--) {
                        RemoveEntity (list[j]);
                    }
                }
            }
        }

        /// <summary>
        /// Detaches component from entity and raise OnComponentDetach event.
        /// </summary>
        /// <param name="entity">Entity.</param>
        /// <param name="componentId">Detaching component.</param>
        void DetachComponent (EcsEntity entity, int componentId) {
            var comp = entity.Components[componentId];
            entity.Components[componentId] = null;
            OnComponentDetach (comp);
            _componentPools[componentId].Recycle (comp);
        }

        /// <summary>
        /// Process delayed updates.
        /// </summary>
        void ProcessDelayedUpdates () {
            var iMax = _delayedUpdates.Count;
            for (var i = 0; i < iMax; i++) {
                var op = _delayedUpdates[i];
                var entityData = _entities[op.Entity];
                var oldMask = entityData.Mask;
                switch (op.Type) {
                    case DelayedUpdate.Op.RemoveEntity:
                        if (!entityData.IsReserved) {
                            var componentId = 0;
                            var empty = new EcsComponentMask ();
                            while (!entityData.Mask.IsEmpty ()) {
                                if (entityData.Mask.GetBit (componentId)) {
                                    entityData.Mask.SetBit (componentId, false);
                                    DetachComponent (entityData, componentId);
                                }
                                componentId++;
                            }
                            UpdateFilters (op.Entity, ref oldMask, ref empty);
                            entityData.IsReserved = true;
                            _reservedEntityIds.Add (op.Entity);
                        }
                        break;
                    case DelayedUpdate.Op.AddComponent:
                        if (!entityData.Mask.GetBit (op.Component)) {
                            entityData.Mask.SetBit (op.Component, true);
                            OnComponentAttach (entityData.Components[op.Component]);
                            UpdateFilters (op.Entity, ref oldMask, ref entityData.Mask);
                        }
                        break;
                    case DelayedUpdate.Op.RemoveComponent:
                        if (entityData.Mask.GetBit (op.Component)) {
                            entityData.Mask.SetBit (op.Component, false);
                            DetachComponent (entityData, op.Component);
                            UpdateFilters (op.Entity, ref oldMask, ref entityData.Mask);
                        }
                        break;
                }
            }
            if (iMax > 0) {
                if (_delayedUpdates.Count == iMax) {
                    _delayedUpdates.Clear ();
                } else {
                    _delayedUpdates.RemoveRange (0, iMax);
                    ProcessDelayedUpdates ();
                }
            }
        }

        /// <summary>
        /// Updates all filters for changed component mask.
        /// </summary>
        /// <param name="entity">Entity.</param>
        /// <param name="oldMask">Old component state.</param>
        /// <param name="newMask">New component state.</param>
        void UpdateFilters (int entity, ref EcsComponentMask oldMask, ref EcsComponentMask newMask) {
            for (var i = _filters.Count - 1; i >= 0; i--) {
                var isNewMaskCompatible = newMask.IsCompatible (_filters[i].Mask);
                if (oldMask.IsCompatible (_filters[i].Mask)) {
                    if (!isNewMaskCompatible) {
                        _filters[i].Entities.Remove (entity);
                    }
                } else {
                    if (isNewMaskCompatible) {
                        _filters[i].Entities.Add (entity);
                    }
                }
            }
        }

        struct DelayedUpdate {
            public enum Op {
                RemoveEntity,
                AddComponent,
                RemoveComponent
            }
            public Op Type;
            public int Entity;
            public int Component;

            public DelayedUpdate (Op type, int entity, int component) {
                Type = type;
                Entity = entity;
                Component = component;
            }
        }

        sealed class EcsEntity {
            public bool IsReserved;
            public EcsComponentMask Mask = new EcsComponentMask ();
            public readonly List<IEcsComponent> Components = new List<IEcsComponent> (64);
        }
    }
}