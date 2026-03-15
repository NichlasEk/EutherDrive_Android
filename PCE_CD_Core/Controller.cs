using System;

namespace ePceCD
{
    public enum PCEKEY
    {
        DPadUp,
        DPadDown,
        DPadLeft,
        DPadRight,
        Select,
        Start,
        A,
        B,
        C,
        X,
        Y,
        Z
    }

    [Serializable]
    public class Controller
    {
        private bool m_SEL;
        private bool m_CLR;

        private bool m_Up;
        private bool m_Down;
        private bool m_Left;
        private bool m_Right;
        private bool m_Button1;
        private bool m_Button2;
        private bool m_Button3;
        private bool m_Button4;
        private bool m_Button5;
        private bool m_Button6;
        private bool m_Run;
        private bool m_Select;
        private bool m_SixButtonMode;
        private bool m_SelectedExtraButtons;

        public Controller()
        {
            m_Up = false;
            m_Down = false;
            m_Left = false;
            m_Right = false;
            m_Button1 = false;
            m_Button2 = false;
            m_Button3 = false;
            m_Button4 = false;
            m_Button5 = false;
            m_Button6 = false;
            m_Run = false;
            m_Select = false;
            m_SixButtonMode = false;
            m_SelectedExtraButtons = false;
        }

        public void SetSixButtonMode(bool enabled)
        {
            m_SixButtonMode = enabled;
            if (!enabled)
                m_SelectedExtraButtons = false;
        }

        public void KeyState(PCEKEY key, short keyup)
        {
            switch (key)
            {
                case PCEKEY.DPadUp:
                    m_Up = (keyup == 0);
                    break;
                case PCEKEY.DPadDown:
                    m_Down = (keyup == 0);
                    break;
                case PCEKEY.DPadRight:
                    m_Right = (keyup == 0);
                    break;
                case PCEKEY.DPadLeft:
                    m_Left = (keyup == 0);
                    break;
                case PCEKEY.B:
                    m_Button1 = (keyup == 0);
                    break;
                case PCEKEY.A:
                    m_Button2 = (keyup == 0);
                    break;
                case PCEKEY.C:
                    m_Button3 = (keyup == 0);
                    break;
                case PCEKEY.X:
                    m_Button4 = (keyup == 0);
                    break;
                case PCEKEY.Y:
                    m_Button5 = (keyup == 0);
                    break;
                case PCEKEY.Z:
                    m_Button6 = (keyup == 0);
                    break;
                case PCEKEY.Start:
                    m_Run = (keyup == 0);
                    break;
                case PCEKEY.Select:
                    m_Select = (keyup == 0);
                    break;
            }
        }

        public void Write(byte data)
        {
            bool prevClr = m_CLR;
            m_CLR = (data & 2) != 0;
            m_SEL = (data & 1) != 0;

            if (m_SixButtonMode)
            {
                if (prevClr && !m_CLR)
                    m_SelectedExtraButtons = !m_SelectedExtraButtons;
            }
            else
            {
                m_SelectedExtraButtons = false;
            }
        }

        public byte Read()
        {
            if (m_CLR)
                return 0xB0;
            else if (m_SixButtonMode && m_SelectedExtraButtons)
            {
                if (m_SEL)
                    return 0xB0;

                return (byte)(
                    0xB0 |
                    (m_Button6 ? 0 : 0x08) |
                    (m_Button5 ? 0 : 0x04) |
                    (m_Button4 ? 0 : 0x02) |
                    (m_Button3 ? 0 : 0x01));
            }
            else if (m_SEL)
                return (byte)(
                    0xB0 |
                    (m_Left ? 0 : 0x08) |
                    (m_Down ? 0 : 0x04) |
                    (m_Right ? 0 : 0x02) |
                    (m_Up ? 0 : 0x01));
            else
                return (byte)(
                    0xB0 |
                    (m_Run ? 0 : 0x08) |
                    (m_Select ? 0 : 0x04) |
                    (m_Button2 ? 0 : 0x02) |
                    (m_Button1 ? 0 : 0x01));
        }
    }
}
