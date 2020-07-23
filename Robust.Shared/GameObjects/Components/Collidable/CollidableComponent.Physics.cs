﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Robust.Shared.Interfaces.Physics;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.GameObjects.Components
{
    public partial interface ICollidableComponent
    {
        /// <summary>
        ///     Current mass of the entity in kilograms.
        /// </summary>
        float Mass { get; set; }

        /// <summary>
        ///     Current linear velocity of the entity in meters per second.
        /// </summary>
        Vector2 LinearVelocity { get; set; }

        /// <summary>
        ///     Current angular velocity of the entity in radians per sec.
        /// </summary>
        float AngularVelocity { get; set; }

        /// <summary>
        ///     Current momentum of the entity in kilogram meters per second
        /// </summary>
        Vector2 Momentum { get; set; }

        /// <summary>
        ///     The current status of the object
        /// </summary>
        BodyStatus Status { get; set; }

        /// <summary>
        ///     Whether this component is on the ground
        /// </summary>
        bool OnGround { get; }

        /// <summary>
        ///     Whether or not the entity is anchored in place.
        /// </summary>
        bool Anchored { get; set; }

        event Action? AnchoredChanged;

        bool Predict { get; set; }

        protected internal Dictionary<Type, VirtualController> Controllers { get; set; }

        T AddController<T>() where T : VirtualController, new();

        T GetController<T>() where T : VirtualController;

        bool TryGetController<T>([MaybeNullWhen(false)] out T controller) where T : VirtualController;

        bool HasController<T>() where T : VirtualController;

        T EnsureController<T>() where T : VirtualController, new();

        bool RemoveController<T>() where T : VirtualController;

        void RemoveControllers();
    }

    partial class CollidableComponent : ICollidableComponent
    {
        private float _mass;
        private Vector2 _linVelocity;
        private float _angVelocity;
        private Dictionary<Type, VirtualController> _controllers = new Dictionary<Type, VirtualController>();
        private bool _anchored;

        /// <summary>
        ///     Current mass of the entity in kilograms.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public float Mass
        {
            get => _mass;
            set
            {
                _mass = value;
                Dirty();
            }
        }

        /// <summary>
        ///     Current linear velocity of the entity in meters per second.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public Vector2 LinearVelocity
        {
            get => _linVelocity;
            set
            {
                if (_linVelocity == value)
                    return;

                _linVelocity = value;
                Dirty();
            }
        }

        /// <summary>
        ///     Current angular velocity of the entity in radians per sec.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public float AngularVelocity
        {
            get => _angVelocity;
            set
            {
                if (_angVelocity.Equals(value))
                    return;

                _angVelocity = value;
                Dirty();
            }
        }

        /// <summary>
        ///     Current momentum of the entity in kilogram meters per second
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public Vector2 Momentum
        {
            get => LinearVelocity * Mass;
            set => LinearVelocity = value / Mass;
        }

        /// <summary>
        ///     The current status of the object
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public BodyStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                Dirty();
            }
        }

        /// <summary>
        ///     Whether this component is on the ground
        /// </summary>
        public bool OnGround => Status == BodyStatus.OnGround &&
                                !IoCManager.Resolve<IPhysicsManager>()
                                    .IsWeightless(Owner.Transform.GridPosition);

        /// <summary>
        ///     Whether or not the entity is anchored in place.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public bool Anchored
        {
            get => _anchored;
            set
            {
                _anchored = value;
                AnchoredChanged?.Invoke();
                Dirty();
            }
        }

        public event Action? AnchoredChanged;

        [ViewVariables(VVAccess.ReadWrite)]
        public bool Predict { get; set; }

        Dictionary<Type, VirtualController> ICollidableComponent.Controllers
        {
            get => _controllers;
            set => _controllers = value;
        }

        [Obsolete("This only exists for legacy reasons.")]
        public bool CanMove([NotNullWhen(true)]IPhysicsComponent physics)
        {
            return Owner.TryGetComponent(out physics) && !Anchored;
        }

        public T AddController<T>() where T : VirtualController, new()
        {
            var controller = new T {ControlledComponent = this};
            _controllers[typeof(T)] = controller;

            Dirty();

            return controller;
        }

        public T GetController<T>() where T : VirtualController
        {
            return (T) _controllers[typeof(T)];
        }

        public bool TryGetController<T>(out T controller) where T : VirtualController
        {
            controller = null!;

            var found = _controllers.TryGetValue(typeof(T), out var value);

            return found && (controller = (value as T)!) != null;
        }

        public bool HasController<T>() where T : VirtualController
        {
            return _controllers.ContainsKey(typeof(T));
        }

        public T EnsureController<T>() where T : VirtualController, new()
        {
            if (TryGetController(out T controller))
            {
                return controller;
            }

            controller = AddController<T>();

            return controller;
        }

        public bool RemoveController<T>() where T : VirtualController
        {
            var removed = _controllers.Remove(typeof(T), out var controller);

            if (controller != null)
            {
                controller.ControlledComponent = null;
            }

            Dirty();

            return removed;
        }

        public void RemoveControllers()
        {
            foreach (var controller in _controllers.Values)
            {
                controller.ControlledComponent = null;
            }

            _controllers.Clear();
            Dirty();
        }
    }
}