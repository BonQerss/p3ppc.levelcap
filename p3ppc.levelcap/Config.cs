using System.ComponentModel;
using p3ppc.levelcap.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace p3ppc.levelcap.Configuration
{
        public class Config : Configurable<Config>
        {
        [Category("Level Caps")]
        [DisplayName("May 9th Cap")]
        [Description("Caps value before Full Moon on 5/9.")]
        [DefaultValue(8)]
        public int May9Cap { get; set; } = 8;

        [Category("Level Caps")]
        [DisplayName("June 8th Cap")]
        [Description("Caps value before Full Moon on 6/8.")]
        [DefaultValue(15)]
        public int June8Cap { get; set; } = 15;

        [Category("Level Caps")]
        [DisplayName("July 7th Cap")]
        [Description("Caps value before Full Moon on 7/7.")]
        [DefaultValue(21)]
        public int July7Cap { get; set; } = 21;

        [Category("Level Caps")]
        [DisplayName("August 6th Cap")]
        [Description("Caps value before Full Moon on 8/6.")]
        [DefaultValue(32)]
        public int Aug6Cap { get; set; } = 32;

        [Category("Level Caps")]
        [DisplayName("September 5th Cap")]
        [Description("Caps value before Full Moon on 9/5.")]
        [DefaultValue(40)]
        public int Sep5Cap { get; set; } = 40;

        [Category("Level Caps")]
        [DisplayName("October 4th Cap")]
        [Description("Caps value before Full Moon on 10/4.")]
        [DefaultValue(46)]
        public int Oct4Cap { get; set; } = 46;

        [Category("Level Caps")]
        [DisplayName("November 3rd Cap")]
        [Description("Caps value before Full Moon on 11/3.")]
        [DefaultValue(54)]
        public int Nov3Cap { get; set; } = 54;

        [Category("Level Caps")]
        [DisplayName("November 22nd Cap")]
        [Description("Caps value before the incident on 5/22.")]
        [DefaultValue(54)]
        public int Nov22Cap { get; set; } = 54;

        [Category("Level Caps")]
        [DisplayName("January 31st Cap")]
        [Description("Caps value before the ultimate end on 1/31.")]
        [DefaultValue(76)]
        public int January31Cap { get; set; } = 76;



        [DisplayName("Debug Mode")]
            [Description("Logs additional information to the console that is useful for debugging.")]
            [DefaultValue(false)]
            public bool DebugEnabled { get; set; } = false;
        }

        /// <summary>
        /// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
        /// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
        /// </summary>
        public class ConfiguratorMixin : ConfiguratorMixinBase
        {
            // 
        }
    }