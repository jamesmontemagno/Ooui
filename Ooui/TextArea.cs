using System;

namespace Ooui
{
    public class TextArea : FormControl
    {
        public event TargetEventHandler Change {
            add => AddEventListener ("change", value);
            remove => RemoveEventListener ("change", value);
        }

        public event TargetEventHandler Input {
            add => AddEventListener ("input", value);
            remove => RemoveEventListener ("input", value);
        }

        string val = "";
        public string Value {
            get => val;
            set => SetProperty (ref val, value ?? "", "value");
        }

        int rows = 2;
        public int Rows {
            get => rows;
            set => SetProperty (ref rows, value, "rows");
        }

        int cols = 20;
        public int Columns {
            get => cols;
            set => SetProperty (ref cols, value, "cols");
        }

        public TextArea ()
            : base ("textarea")
        {
            // Subscribe to the change event so we always get up-to-date values
            Change += (s, e) => {};
        }

        public TextArea (string text)
            : this ()
        {
            Value = text;
        }

        protected override bool TriggerEventFromMessage (Message message)
        {
            if (message.TargetId == Id && message.MessageType == MessageType.Event && (message.Key == "change" || message.Key == "input")) {
                // Don't need to notify here because the base implementation will fire the event
                val = message.Value != null ? Convert.ToString (message.Value) : "";
            }
            return base.TriggerEventFromMessage (message);
        }
    }
}
