// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using size_t = System.UInt64;


namespace mame
{
    public static class drivlist_global_generated
    {
        public static void init()
        {
            //&GAME_NAME(___empty),
            drivlist_global.s_drivers_sorted = new game_driver []
            {
                ___empty.driver____empty,
                _1942.driver_1942,
                asteroid.driver_asteroid,
                //asteroid.driver_astdelux,
                atarisy2.driver_720,
                atarisy2.driver_paperboy,
                //berzerk.driver_berzerk,
                batsugun.driver_batsugunsp,
                btime.driver_btime,
                bzone.driver_bzone,
                cclimber.driver_cclimber,
                centiped.driver_centipede,
                centiped.driver_milliped,
                dkong.driver_dkong,
                dkong.driver_dkong3,
                dkong.driver_dkongjr,
                fastfred.driver_fastfred,
                fastfred.driver_flyboy,
                galaga.driver_digdug,
                galaga.driver_galaga,
                galaga.driver_xevious,
                galaxian.driver_frogger,
                galaxian.driver_galaxian,
                //gottlieb.driver_qbert,
                hshavoc.driver_hshavoc,
                pgm.driver_ddpdoj,
                pgm.driver_espgal,
                pgm.driver_ket,
                pgm.driver_kov,
                pgm.driver_kov2,
                pgm.driver_orlegend,
                pgm.driver_dmnfrnt,
                pgm.driver_theglad,
                m52.driver_mpatrol,
                //mappy.driver_superpac,
                mcr.driver_demoderb,
                mcr.driver_dotron,
                mcr.driver_tapper,
                mcr.driver_tron,
                mcr.driver_twotiger,
                mcr3.driver_rampage,
                mcr3.driver_rampage2,
                missile.driver_missile,
                mw8080bw.driver_gunfight,
                mw8080bw.driver_invaders,
                neogeo.driver_neogeo,
                neogeo.driver_nam1975,
                neogeo.driver_mslug,
                pacman.driver_mspacman,
                pacman.driver_pacman,
                pacman.driver_pacplus,
                pacman.driver_puckman,
                //pengo.driver_pengo,
                pgm.driver_pgm,
                polepos.driver_polepos,
                pong.driver_pong,
                //popeye.driver_popeye,
                rallyx.driver_rallyx,
                //snk6502.driver_vanguard,
                system1.driver_choplift,
                taitosj.driver_elevator,
                taitosj.driver_jungleh,
                taitosj.driver_junglek,
                //tempest.driver_tempest,
                toaplan1.driver_outzone,
                //turbo.driver_turbo,
                williams.driver_defender,
                williams.driver_joust,
                williams.driver_robotron,
                williams.driver_sinistar,
                williams.driver_stargate,
                //zaxxon.driver_zaxxon,
                xain.driver_xsleena,
                xain.driver_xsleenaj,
                xain.driver_solrwarr,
                xain.driver_xsleenab,
            };

            drivlist_global.s_driver_count = (size_t)drivlist_global.s_drivers_sorted.Length;
        }
    }
}
