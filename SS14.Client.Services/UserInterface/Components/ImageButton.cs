﻿using SS14.Client.Interfaces.Resource;
using SS14.Client.Graphics.Sprite;
using SS14.Shared.IoC;
using System;
using SFML.Window;
using SFML.Graphics;
using System.Drawing;
using SS14.Shared.Maths;
using Color = SFML.Graphics.Color;

namespace SS14.Client.Services.UserInterface.Components
{
    public class ImageButton : GuiComponent
    {
        #region Delegates

        public delegate void ImageButtonPressHandler(ImageButton sender);

        #endregion

        private readonly IResourceManager _resourceManager;
        private Sprite _buttonClick;
        private Sprite _buttonHover;
        private Sprite _buttonNormal;

        private Sprite _drawSprite;

        public ImageButton()
        {
            _resourceManager = IoCManager.Resolve<IResourceManager>();
            Color = Color.White;
            Update(0);
        }

        public Color Color { get; set; }

        public string ImageNormal
        {
            set { _buttonNormal = _resourceManager.GetSprite(value); }
        }

        public string ImageHover
        {
            set { _buttonHover = _resourceManager.GetSprite(value); }
        }

        public string ImageClick
        {
            set { _buttonClick = _resourceManager.GetSprite(value); }
        }

        public event ImageButtonPressHandler Clicked;

        public override sealed void Update(float frameTime)
        {
            if (_drawSprite == null && _buttonNormal != null)
                _drawSprite = _buttonNormal;

            if (_drawSprite != null)
            {
                _drawSprite.Position = new Vector2( Position.X,Position.Y);
                var bounds = _drawSprite.GetLocalBounds();
                ClientArea = new Rectangle(Position,
                                           new Size((int)bounds.Width, (int)bounds.Height));
            }
        }

        public override void Render()
        {
            if (_drawSprite != null)
            {
                _drawSprite.Color = Color;
                _drawSprite.Position = new Vector2 (Position.X,Position.Y);
                _drawSprite.Texture.Smooth = true;
                _drawSprite.Draw(Graphics.CluwneLib.CurrentRenderTarget, new RenderStates(BlendMode.Alpha));
                _drawSprite.Color = Color;
            }
        }

        public override void Dispose()
        {
            _buttonNormal = null;
            _buttonHover = null;
            _buttonClick = null;
            Clicked = null;
            base.Dispose();
            GC.SuppressFinalize(this);
        }

        public override void MouseMove(MouseMoveEventArgs e)
        {
            if (ClientArea.Contains(new Point((int)e.X, (int)e.Y)) && _buttonHover != null)
            {
                if (_drawSprite != _buttonClick)
                    _drawSprite = _buttonHover;
            }
            else
            {
                if (_drawSprite != _buttonClick)
                    _drawSprite = _buttonNormal;
            }
        }

        public override bool MouseDown(MouseButtonEventArgs e)
        {
            if (ClientArea.Contains(new Point((int)e.X, (int)e.Y)))
            {
                if (_buttonClick != null) _drawSprite = _buttonClick;
                if (Clicked != null) Clicked(this);
                return true;
            }
            return false;
        }

        public override bool MouseUp(MouseButtonEventArgs e)
        {
            if (_drawSprite == _buttonClick)
                if (_buttonHover != null)
                    _drawSprite = ClientArea.Contains(new Point((int)e.X, (int)e.Y))
                                      ? _buttonHover
                                      : _buttonNormal;
                else
                    _drawSprite = _buttonNormal;
            return false;
        }
    }
}